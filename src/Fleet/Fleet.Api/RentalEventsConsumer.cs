using System.Text;
using Confluent.Kafka;
using Shared.Contracts;

namespace Fleet.Api;

public sealed class KafkaConsumerOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = "localhost:9092";

    public string Topic { get; set; } = KafkaTopics.RentalEvents;

    public string GroupId { get; set; } = "fleet-service";

    /// <summary>Permite apagar el consumidor en pruebas de API que no necesitan broker.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Adaptador de entrada dirigido por mensajes. Su unica responsabilidad es
/// traer bytes de Kafka, deserializarlos usando la cabecera event-type y
/// delegar en el handler. Cero reglas de negocio aqui.
/// </summary>
public sealed class RentalEventsConsumer(
    Microsoft.Extensions.Options.IOptions<KafkaConsumerOptions> options,
    IServiceScopeFactory scopeFactory,
    ILogger<RentalEventsConsumer> logger) : BackgroundService
{
    private readonly KafkaConsumerOptions _options = options.Value;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Kafka consumer disabled by configuration.");
            return Task.CompletedTask;
        }

        // Consume() es bloqueante: se aisla en un hilo propio para no frenar
        // el arranque del host.
        return Task.Factory.StartNew(
            () => ConsumeLoop(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private async Task ConsumeLoop(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.GroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true,
            AllowAutoCreateTopics = true
        };

        using var consumer = new ConsumerBuilder<string, string>(config)
            .SetErrorHandler((_, error) => logger.LogWarning("Kafka error: {Reason}", error.Reason))
            .Build();

        consumer.Subscribe(_options.Topic);
        logger.LogInformation("Subscribed to {Topic} as {GroupId}.", _options.Topic, _options.GroupId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(TimeSpan.FromMilliseconds(500));
                if (result?.Message is null)
                {
                    continue;
                }

                var eventType = ReadEventType(result.Message);
                if (eventType is null)
                {
                    logger.LogWarning("Message without event-type header, skipped.");
                    continue;
                }

                var integrationEvent = EventSerialization.Deserialize(eventType, result.Message.Value);
                if (integrationEvent is null)
                {
                    logger.LogWarning("Unknown event type {EventType}, skipped.", eventType);
                    continue;
                }

                using var scope = scopeFactory.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<IVehicleAvailabilityHandler>();
                await handler.HandleAsync(integrationEvent, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to process a rental event.");
            }
        }

        consumer.Close();
    }

    private static string? ReadEventType(Message<string, string> message)
    {
        if (message.Headers is null)
        {
            return null;
        }

        return message.Headers.TryGetLastBytes(EventHeaders.EventType, out var bytes)
            ? Encoding.UTF8.GetString(bytes)
            : null;
    }
}

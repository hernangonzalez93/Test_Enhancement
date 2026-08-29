using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Shared.Contracts;

namespace Notifications.Api;

public sealed class KafkaConsumerOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = "localhost:9092";

    public string Topic { get; set; } = KafkaTopics.RentalEvents;

    public string GroupId { get; set; } = "notifications-service";

    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Segundo consumidor del mismo topico, con un grupo distinto: Fleet y
/// Notifications reciben cada evento de forma independiente. Ese fan-out es
/// exactamente lo que verifica la prueba de integracion sobre Kafka.
/// </summary>
public sealed class RentalEventsConsumer(
    IOptions<KafkaConsumerOptions> options,
    INotificationIngestor ingestor,
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

                if (!result.Message.Headers.TryGetLastBytes(EventHeaders.EventType, out var typeBytes))
                {
                    logger.LogWarning("Message without event-type header, skipped.");
                    continue;
                }

                var eventType = Encoding.UTF8.GetString(typeBytes);
                var integrationEvent = EventSerialization.Deserialize(eventType, result.Message.Value);
                if (integrationEvent is null)
                {
                    logger.LogWarning("Unknown event type {EventType}, skipped.", eventType);
                    continue;
                }

                await ingestor.IngestAsync(integrationEvent, stoppingToken);
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
}

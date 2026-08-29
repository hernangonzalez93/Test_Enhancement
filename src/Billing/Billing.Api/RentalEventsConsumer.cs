using System.Text;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Options;
using Shared.Contracts;

using Billing.Application;

namespace Billing.Api;

public sealed class KafkaConsumerOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = "localhost:9092";

    public string Topic { get; set; } = KafkaTopics.RentalEvents;

    public string GroupId { get; set; } = "billing-service";

    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Cuarto consumidor del mismo topico. A diferencia de los otros, Billing usa
/// un servicio con ambito (scoped) porque detras hay un DbContext, de ahi el
/// IServiceScopeFactory: cada mensaje se procesa en su propio ambito.
/// </summary>
public sealed class RentalEventsConsumer(
    IOptions<KafkaConsumerOptions> options,
    IServiceScopeFactory scopeFactory,
    ConsumerReadiness readiness,
    ILogger<RentalEventsConsumer> logger) : BackgroundService
{
    private readonly KafkaConsumerOptions _options = options.Value;

    private Thread? _worker;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Kafka consumer disabled by configuration.");
            // Apagado por configuracion: no hay nada que esperar.
            readiness.MarkReady();
            return Task.CompletedTask;
        }

        // Hilo dedicado y bucle sincrono. Ver el comentario equivalente en
        // Fleet.Api: con un lambda async, el Consume() bloqueante acaba
        // ejecutandose sobre el thread pool y se detiene cuando el pool se agota.
        _worker = new Thread(() => ConsumeLoop(stoppingToken))
        {
            IsBackground = true,
            Name = "billing-rental-events-consumer"
        };

        _worker.Start();

        return Task.CompletedTask;
    }

    private void ConsumeLoop(CancellationToken stoppingToken)
    {
        EnsureTopicExists();

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
            .SetPartitionsAssignedHandler((_, partitions) =>
            {
                // Recibir particiones es la primera prueba de que este
                // consumidor va a ver mensajes. Hasta aqui, /health/ready falla.
                readiness.MarkReady();
                logger.LogInformation(
                    "Partitions assigned: {Partitions}",
                    string.Join(", ", partitions.Select(partition => partition.Partition.Value)));
            })
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

                using var scope = scopeFactory.CreateScope();
                var invoices = scope.ServiceProvider.GetRequiredService<IInvoiceService>();
                InvoiceFromEvent(invoices, integrationEvent, stoppingToken).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ConsumeException exception)
            {
                logger.LogWarning("Kafka consume error: {Reason}", exception.Error.Reason);
                Thread.Sleep(500);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to process a rental event.");
            }
        }

        consumer.Close();
    }

    /// <summary>
    /// Crea el topico si no existe, de forma idempotente.
    ///
    /// Sin esto, en un cluster recien arrancado nadie ha publicado todavia, el
    /// topico no existe y el consumidor nunca recibe particiones: se queda
    /// suscrito a la nada hasta que llega el primer mensaje. Depender de
    /// `auto.create.topics.enable` no es una opcion seria porque en clusters
    /// reales suele estar desactivado.
    /// </summary>
    private void EnsureTopicExists()
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig
        {
            BootstrapServers = _options.BootstrapServers
        }).Build();

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                admin.CreateTopicsAsync([
                    new TopicSpecification
                    {
                        Name = _options.Topic,
                        NumPartitions = 1,
                        ReplicationFactor = 1
                    }
                ]).GetAwaiter().GetResult();

                logger.LogInformation("Topic {Topic} created.", _options.Topic);
                return;
            }
            catch (CreateTopicsException exception)
                when (exception.Results.All(result => result.Error.Code == ErrorCode.TopicAlreadyExists))
            {
                logger.LogInformation("Topic {Topic} already exists.", _options.Topic);
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Could not ensure topic {Topic} (attempt {Attempt}/5).",
                    _options.Topic,
                    attempt);
                Thread.Sleep(TimeSpan.FromSeconds(2));
            }
        }
    }

    /// <summary>
    /// Traduce el evento al caso de uso correspondiente. Facturar es una
    /// decision de negocio, asi que la toma la capa de aplicacion; aqui solo se
    /// elige el metodo.
    /// </summary>
    private static Task InvoiceFromEvent(
        IInvoiceService invoices,
        IIntegrationEvent integrationEvent,
        CancellationToken cancellationToken) => integrationEvent switch
    {
        RentalCompletedIntegrationEvent e => invoices.IssueForCompletedRentalAsync(
            e.RentalId, e.CustomerId, e.FinalTotal, e.LateDays, e.Currency, cancellationToken),

        RentalCancelledIntegrationEvent e => invoices.IssueForCancelledRentalAsync(
            e.RentalId, e.CustomerId, e.EstimatedTotal, e.RefundAmount, e.Currency, cancellationToken),

        _ => Task.CompletedTask
    };

}

using System.Text;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
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

        // Hilo dedicado y bucle SINCRONO, no Task.Factory.StartNew sobre un
        // lambda async.
        //
        // Con `StartNew(() => ConsumeLoopAsync(...), TaskCreationOptions.LongRunning)`
        // el hilo dedicado solo ejecuta hasta el primer await: a partir del
        // primer mensaje, el Consume() bloqueante pasa a correr sobre hilos del
        // thread pool. Bajo carga (por ejemplo la suite de pruebas completa, que
        // ejecuta varios proyectos en paralelo) el pool se agota, el bucle deja
        // de recibir hilo y el consumidor se detiene durante segundos.
        //
        // Un consumidor de Kafka debe ser dueno de su hilo.
        _worker = new Thread(() => ConsumeLoop(stoppingToken))
        {
            IsBackground = true,
            Name = "fleet-rental-events-consumer"
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

                // El identificador de correlacion viene de quien publico, y abre un
                // ambito que acompana a TODAS las lineas que escriba el manejo de
                // este mensaje. Es lo que permite unir, en una sola busqueda, la
                // peticion HTTP original con lo que ocurrio al otro lado del broker.
                using var correlationScope = result.Message.Headers
                    .TryGetLastBytes(EventHeaders.CorrelationId, out var correlationBytes)
                        ? logger.BeginScope(new Dictionary<string, object>
                            { ["CorrelationId"] = Encoding.UTF8.GetString(correlationBytes) })
                        : null;

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

                // El hilo es propio y no hay SynchronizationContext, asi que
                // esperar de forma sincrona aqui es seguro y mantiene el bucle
                // dentro de este hilo.
                handler.HandleAsync(integrationEvent, stoppingToken).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ConsumeException exception)
            {
                // Tipicamente "topic no disponible todavia". Se espera un poco
                // para no convertir el bucle en una espera activa.
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

}

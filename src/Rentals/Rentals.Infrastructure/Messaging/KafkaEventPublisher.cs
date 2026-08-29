using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rentals.Application.Abstractions;
using Shared.Contracts;

namespace Rentals.Infrastructure.Messaging;

/// <summary>
/// Adaptador de salida hacia Kafka. Decide tres cosas que forman parte del
/// contrato con los consumidores y que por eso tienen prueba de integracion:
/// el topico, la clave de particion (id de renta, para preservar el orden por
/// renta) y la cabecera event-type que permite enrutar sin deserializar.
/// </summary>
public sealed class KafkaEventPublisher : IEventPublisher, IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly KafkaOptions _options;
    private readonly ILogger<KafkaEventPublisher> _logger;

    public KafkaEventPublisher(IOptions<KafkaOptions> options, ILogger<KafkaEventPublisher> logger)
    {
        _options = options.Value;
        _logger = logger;

        var config = new ProducerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            MessageTimeoutMs = _options.MessageTimeoutMs,
            Acks = Acks.All,
            EnableIdempotence = true
        };

        _producer = new ProducerBuilder<string, string>(config).Build();
    }

    public async Task PublishAsync(
        IReadOnlyCollection<IIntegrationEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        foreach (var @event in events)
        {
            var message = new Message<string, string>
            {
                Key = @event.PartitionKey,
                Value = EventSerialization.Serialize(@event),
                Headers =
                [
                    new Header(EventHeaders.EventType, Encoding.UTF8.GetBytes(@event.EventType)),
                    new Header(EventHeaders.EventId, Encoding.UTF8.GetBytes(@event.EventId.ToString()))
                ]
            };

            var result = await _producer.ProduceAsync(_options.RentalEventsTopic, message, cancellationToken);

            _logger.LogInformation(
                "Published {EventType} for key {Key} to {TopicPartitionOffset}.",
                @event.EventType,
                @event.PartitionKey,
                result.TopicPartitionOffset);
        }
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}

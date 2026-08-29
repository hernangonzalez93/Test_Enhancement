using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rentals.Infrastructure.Messaging;
using Shared.Contracts;

namespace Rentals.Infrastructure.Tests;

/// <summary>
/// El adaptador de mensajeria contra un broker real. Aqui se congela el
/// contrato de transporte: topico, clave de particion, cabeceras y JSON.
/// Un mock nunca detectaria, por ejemplo, que la cabecera se escribe con otro
/// nombre del que lee el consumidor.
/// </summary>
[Collection(KafkaCollection.Name)]
public sealed class KafkaEventPublisherTests(KafkaFixture fixture)
{
    private const string Topic = "rental-events-test";

    [Fact]
    public async Task Publishes_the_event_with_the_rental_id_as_key()
    {
        var @event = SampleRequestedEvent();

        await PublishAsync(@event);

        var message = Consume(1).Single();
        message.Message.Key.ShouldBe(@event.RentalId.ToString());
    }

    [Fact]
    public async Task Publishes_the_event_type_in_a_header_so_consumers_can_route_it()
    {
        var @event = SampleRequestedEvent();

        await PublishAsync(@event);

        var message = Consume(1).Single();
        message.Message.Headers.TryGetLastBytes(EventHeaders.EventType, out var typeBytes).ShouldBeTrue();
        Encoding.UTF8.GetString(typeBytes).ShouldBe(IntegrationEventTypes.RentalRequested);

        message.Message.Headers.TryGetLastBytes(EventHeaders.EventId, out var idBytes).ShouldBeTrue();
        Guid.Parse(Encoding.UTF8.GetString(idBytes)).ShouldBe(@event.EventId);
    }

    [Fact]
    public async Task The_published_payload_deserializes_back_into_the_same_event()
    {
        var @event = SampleRequestedEvent();

        await PublishAsync(@event);

        var message = Consume(1).Single();
        var roundTripped = EventSerialization
            .Deserialize(IntegrationEventTypes.RentalRequested, message.Message.Value)
            .ShouldBeOfType<RentalRequestedIntegrationEvent>();

        roundTripped.RentalId.ShouldBe(@event.RentalId);
        roundTripped.CustomerId.ShouldBe(@event.CustomerId);
        roundTripped.EstimatedTotal.ShouldBe(@event.EstimatedTotal);
        roundTripped.Currency.ShouldBe(@event.Currency);
        roundTripped.PeriodStart.ShouldBe(@event.PeriodStart);
    }

    [Fact]
    public async Task A_batch_keeps_the_order_of_the_events_within_the_same_rental()
    {
        var rentalId = Guid.CreateVersion7();
        var customerId = Guid.CreateVersion7();
        var vehicleId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        await PublishAsync(
            new RentalRequestedIntegrationEvent(rentalId, customerId, vehicleId, now, now.AddDays(3), 150m, "USD", now),
            new RentalConfirmedIntegrationEvent(rentalId, customerId, vehicleId, 150m, "USD", now),
            new RentalCancelledIntegrationEvent(rentalId, customerId, vehicleId, 150m, 150m, 0m, 100m, "USD", now));

        var types = Consume(3)
            .Select(m =>
            {
                m.Message.Headers.TryGetLastBytes(EventHeaders.EventType, out var bytes);
                return Encoding.UTF8.GetString(bytes);
            })
            .ToArray();

        types.ShouldBe(
        [
            IntegrationEventTypes.RentalRequested,
            IntegrationEventTypes.RentalConfirmed,
            IntegrationEventTypes.RentalCancelled
        ]);
    }

    [Fact]
    public async Task Publishing_an_empty_batch_adds_no_message_to_the_topic()
    {
        using var publisher = CreatePublisher(out var topic);
        await publisher.PublishAsync([SampleRequestedEvent()]);

        await publisher.PublishAsync([]);

        // Se pide una segunda: si el lote vacio hubiera producido algo, llegaria.
        Consume(2, topic, TimeSpan.FromSeconds(5)).Count.ShouldBe(1);
    }

    private static RentalRequestedIntegrationEvent SampleRequestedEvent()
    {
        var now = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);

        return new RentalRequestedIntegrationEvent(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            now.AddDays(10),
            now.AddDays(13),
            150m,
            "USD",
            now);
    }

    private string _currentTopic = Topic;

    private KafkaEventPublisher CreatePublisher(out string topic)
    {
        // Guid.NewGuid y no CreateVersion7: los UUID v7 comparten prefijo temporal,
        // asi que sus primeros caracteres coinciden y todas las pruebas acabarian
        // publicando en el mismo topico.
        topic = Topic + "-" + Guid.NewGuid().ToString("N")[..8];
        _currentTopic = topic;

        return new KafkaEventPublisher(
            Options.Create(new KafkaOptions
            {
                BootstrapServers = fixture.BootstrapServers,
                RentalEventsTopic = topic,
                MessageTimeoutMs = 10000
            }),
            NullLogger<KafkaEventPublisher>.Instance);
    }

    private async Task PublishAsync(params IIntegrationEvent[] events)
    {
        using var publisher = CreatePublisher(out _);
        await publisher.PublishAsync(events);
    }

    private List<ConsumeResult<string, string>> Consume(
        int expected,
        string? topic = null,
        TimeSpan? timeout = null)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = fixture.BootstrapServers,
            GroupId = "test-" + Guid.NewGuid().ToString("N")[..8],
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            AllowAutoCreateTopics = true
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(topic ?? _currentTopic);

        var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(30));
        var results = new List<ConsumeResult<string, string>>();

        while (results.Count < expected && DateTime.UtcNow < deadline)
        {
            var result = consumer.Consume(TimeSpan.FromMilliseconds(500));
            if (result?.Message is not null)
            {
                results.Add(result);
            }
        }

        consumer.Close();
        return results;
    }
}

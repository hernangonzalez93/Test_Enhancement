using System.Text.Json;
using Shared.Contracts;

namespace Rentals.Infrastructure.Tests;

/// <summary>
/// Pruebas de contrato del mensaje. No hay red ni Docker: se congela la FORMA
/// del JSON y los nombres de los tipos de evento, que es lo que comparten
/// Rentals, Fleet y Notifications. Si alguien renombra una propiedad, falla
/// aqui y no en produccion tres semanas despues.
/// </summary>
public sealed class EventContractTests
{
    [Theory]
    [InlineData(IntegrationEventTypes.RentalRequested, "rental.requested")]
    [InlineData(IntegrationEventTypes.RentalConfirmed, "rental.confirmed")]
    [InlineData(IntegrationEventTypes.RentalCancelled, "rental.cancelled")]
    [InlineData(IntegrationEventTypes.RentalStarted, "rental.started")]
    [InlineData(IntegrationEventTypes.RentalCompleted, "rental.completed")]
    public void Event_type_names_are_frozen(string actual, string expected) => actual.ShouldBe(expected);

    [Fact]
    public void The_topic_name_is_frozen() => KafkaTopics.RentalEvents.ShouldBe("rental-events");

    [Fact]
    public void A_requested_event_serializes_with_camel_case_property_names()
    {
        var now = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var @event = new RentalRequestedIntegrationEvent(
            Guid.Parse("0198b0a0-0000-7000-8000-000000000001"),
            Guid.Parse("0198b0a0-0000-7000-8000-000000000002"),
            Guid.Parse("0198b0a0-0000-7000-8000-000000000003"),
            now.AddDays(10),
            now.AddDays(13),
            150m,
            "USD",
            now);

        using var document = JsonDocument.Parse(EventSerialization.Serialize(@event));
        var root = document.RootElement;

        root.GetProperty("rentalId").GetGuid().ShouldBe(@event.RentalId);
        root.GetProperty("customerId").GetGuid().ShouldBe(@event.CustomerId);
        root.GetProperty("vehicleId").GetGuid().ShouldBe(@event.VehicleId);
        root.GetProperty("estimatedTotal").GetDecimal().ShouldBe(150m);
        root.GetProperty("currency").GetString().ShouldBe("USD");
        root.TryGetProperty("eventId", out _).ShouldBeTrue();
    }

    [Fact]
    public void A_cancellation_carries_the_total_besides_the_refund()
    {
        // Billing necesita el total para calcular la penalizacion no reembolsada:
        // con solo el porcentaje no se puede derivar cuando el reembolso es cero.
        var now = DateTimeOffset.UtcNow;
        var original = new RentalCancelledIntegrationEvent(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 300m, 150m, 50m, "USD", now);

        var json = EventSerialization.Serialize(original);
        using var document = JsonDocument.Parse(json);

        document.RootElement.GetProperty("estimatedTotal").GetDecimal().ShouldBe(300m);
        document.RootElement.GetProperty("refundAmount").GetDecimal().ShouldBe(150m);

        EventSerialization.Deserialize(IntegrationEventTypes.RentalCancelled, json)
            .ShouldBeOfType<RentalCancelledIntegrationEvent>()
            .EstimatedTotal.ShouldBe(300m);
    }

    [Fact]
    public void Deserialize_dispatches_on_the_event_type()
    {
        var now = DateTimeOffset.UtcNow;
        var original = new RentalCompletedIntegrationEvent(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 250m, 2, "USD", now);

        var json = EventSerialization.Serialize(original);
        var restored = EventSerialization
            .Deserialize(IntegrationEventTypes.RentalCompleted, json)
            .ShouldBeOfType<RentalCompletedIntegrationEvent>();

        restored.FinalTotal.ShouldBe(250m);
        restored.LateDays.ShouldBe(2);
    }

    [Fact]
    public void Deserialize_returns_null_for_an_unknown_event_type()
    {
        EventSerialization.Deserialize("rental.teleported", "{}").ShouldBeNull();
    }

    [Fact]
    public void Every_event_partitions_by_rental_id()
    {
        var rentalId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        IIntegrationEvent[] events =
        [
            new RentalRequestedIntegrationEvent(rentalId, Guid.CreateVersion7(), Guid.CreateVersion7(), now, now, 1m, "USD", now),
            new RentalConfirmedIntegrationEvent(rentalId, Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, "USD", now),
            new RentalCancelledIntegrationEvent(rentalId, Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 1m, 100m, "USD", now),
            new RentalStartedIntegrationEvent(rentalId, Guid.CreateVersion7(), Guid.CreateVersion7(), now, now),
            new RentalCompletedIntegrationEvent(rentalId, Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 0, "USD", now)
        ];

        events.ShouldAllBe(e => e.PartitionKey == rentalId.ToString());
    }
}

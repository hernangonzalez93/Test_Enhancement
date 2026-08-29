namespace Shared.Contracts;

/// <summary>Nombres estables de los topicos de Kafka.</summary>
public static class KafkaTopics
{
    public const string RentalEvents = "rental-events";
}

/// <summary>
/// Tipos de evento publicados por el servicio de rentas. Son parte del contrato
/// publico: cambiarlos rompe a Fleet y a Notifications, por eso hay una prueba
/// de contrato que los congela.
/// </summary>
public static class IntegrationEventTypes
{
    public const string RentalRequested = "rental.requested";
    public const string RentalConfirmed = "rental.confirmed";
    public const string RentalCancelled = "rental.cancelled";
    public const string RentalStarted = "rental.started";
    public const string RentalCompleted = "rental.completed";
}

/// <summary>Cabecera de Kafka que transporta el tipo de evento para enrutar sin deserializar.</summary>
public static class EventHeaders
{
    public const string EventType = "event-type";
    public const string EventId = "event-id";
    public const string CorrelationId = "correlation-id";
}

public interface IIntegrationEvent
{
    Guid EventId { get; }

    string EventType { get; }

    DateTimeOffset OccurredAt { get; }

    /// <summary>Clave de particion: garantiza orden por renta dentro del topico.</summary>
    string PartitionKey { get; }
}

public sealed record RentalRequestedIntegrationEvent(
    Guid RentalId,
    Guid CustomerId,
    Guid VehicleId,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    decimal EstimatedTotal,
    string Currency,
    DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public Guid EventId { get; init; } = Guid.CreateVersion7();

    public string EventType => IntegrationEventTypes.RentalRequested;

    public string PartitionKey => RentalId.ToString();
}

public sealed record RentalConfirmedIntegrationEvent(
    Guid RentalId,
    Guid CustomerId,
    Guid VehicleId,
    decimal EstimatedTotal,
    string Currency,
    DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public Guid EventId { get; init; } = Guid.CreateVersion7();

    public string EventType => IntegrationEventTypes.RentalConfirmed;

    public string PartitionKey => RentalId.ToString();
}

public sealed record RentalCancelledIntegrationEvent(
    Guid RentalId,
    Guid CustomerId,
    Guid VehicleId,
    decimal RefundAmount,
    decimal RefundPercentage,
    string Currency,
    DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public Guid EventId { get; init; } = Guid.CreateVersion7();

    public string EventType => IntegrationEventTypes.RentalCancelled;

    public string PartitionKey => RentalId.ToString();
}

public sealed record RentalStartedIntegrationEvent(
    Guid RentalId,
    Guid CustomerId,
    Guid VehicleId,
    DateTimeOffset PickedUpAt,
    DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public Guid EventId { get; init; } = Guid.CreateVersion7();

    public string EventType => IntegrationEventTypes.RentalStarted;

    public string PartitionKey => RentalId.ToString();
}

public sealed record RentalCompletedIntegrationEvent(
    Guid RentalId,
    Guid CustomerId,
    Guid VehicleId,
    decimal FinalTotal,
    int LateDays,
    string Currency,
    DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public Guid EventId { get; init; } = Guid.CreateVersion7();

    public string EventType => IntegrationEventTypes.RentalCompleted;

    public string PartitionKey => RentalId.ToString();
}

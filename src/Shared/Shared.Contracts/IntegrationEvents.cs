using System.Diagnostics;
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
    public const string RentalExtended = "rental.extended";
}

/// <summary>Cabeceras de Kafka que acompanan a cada mensaje.</summary>
public static class EventHeaders
{
    /// <summary>Permite a los consumidores enrutar sin deserializar el cuerpo.</summary>
    public const string EventType = "event-type";

    /// <summary>Identidad del evento, para detectar reprocesos.</summary>
    public const string EventId = "event-id";

    /// <summary>
    /// Cose las dos mitades de una operacion que cruza el broker. Sin ella, los
    /// logs de quien publica y de quien consume no tienen nada en comun.
    /// </summary>
    public const string CorrelationId = "correlation-id";
}

/// <summary>
/// Transporta el identificador de correlacion a traves del <see cref="Activity"/>
/// en curso, que .NET ya propaga por todo el arbol de llamadas asincronas.
///
/// La alternativa —inyectar un accesor por todas las capas— obligaria a que la
/// infraestructura de mensajeria conociese algo del transporte HTTP. Aqui no
/// hace falta: el middleware lo deposita, el publicador lo recoge, y ninguno de
/// los dos sabe del otro.
/// </summary>
public static class CorrelationContext
{
    private const string BaggageKey = "correlation.id";

    public static string? Current => Activity.Current?.GetBaggageItem(BaggageKey);

    public static void Set(string correlationId) =>
        Activity.Current?.SetBaggage(BaggageKey, correlationId);
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
    decimal EstimatedTotal,
    decimal RefundAmount,
    /// <summary>Importe que queda por cobrar. Cero si la renta nunca se confirmo.</summary>
    decimal PenaltyAmount,
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

public sealed record RentalExtendedIntegrationEvent(
    Guid RentalId,
    Guid CustomerId,
    Guid VehicleId,
    DateTimeOffset NewPeriodEnd,
    decimal NewEstimatedTotal,
    string Currency,
    DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public Guid EventId { get; init; } = Guid.CreateVersion7();

    public string EventType => IntegrationEventTypes.RentalExtended;

    public string PartitionKey => RentalId.ToString();
}

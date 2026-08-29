using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shared.Contracts;

/// <summary>
/// Un unico lugar decide como viaja el JSON por Kafka. Si productor y consumidor
/// usaran opciones distintas, el bug seria invisible en pruebas unitarias:
/// por eso hay una prueba de contrato de serializacion.
/// </summary>
public static class EventSerialization
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public static string Serialize(IIntegrationEvent @event) =>
        JsonSerializer.Serialize(@event, @event.GetType(), Options);

    public static IIntegrationEvent? Deserialize(string eventType, string json) => eventType switch
    {
        IntegrationEventTypes.RentalRequested =>
            JsonSerializer.Deserialize<RentalRequestedIntegrationEvent>(json, Options),
        IntegrationEventTypes.RentalConfirmed =>
            JsonSerializer.Deserialize<RentalConfirmedIntegrationEvent>(json, Options),
        IntegrationEventTypes.RentalCancelled =>
            JsonSerializer.Deserialize<RentalCancelledIntegrationEvent>(json, Options),
        IntegrationEventTypes.RentalStarted =>
            JsonSerializer.Deserialize<RentalStartedIntegrationEvent>(json, Options),
        IntegrationEventTypes.RentalCompleted =>
            JsonSerializer.Deserialize<RentalCompletedIntegrationEvent>(json, Options),
        _ => null
    };
}

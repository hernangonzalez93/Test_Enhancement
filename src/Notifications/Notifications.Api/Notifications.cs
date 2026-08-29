using System.Collections.Concurrent;
using Shared.Contracts;

namespace Notifications.Api;

/// <summary>
/// Aviso al cliente. Su <see cref="Id"/> es el identificador del evento de
/// integracion que lo origino, no un valor nuevo: eso es lo que hace detectable
/// un reproceso.
/// </summary>
public sealed record Notification(
    Guid Id,
    Guid RentalId,
    Guid CustomerId,
    string EventType,
    string Message,
    DateTimeOffset CreatedAt);

public interface INotificationStore
{
    /// <summary>
    /// Guarda la notificacion. Devuelve false si ya habia una con el mismo Id,
    /// en cuyo caso no se almacena nada.
    /// </summary>
    Task<bool> AddAsync(Notification notification, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Notification>> ListAsync(Guid? customerId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Almacen en memoria: Notifications es deliberadamente el servicio mas simple
/// del sistema, para que las pruebas se concentren en el flujo de eventos y no
/// en su persistencia.
///
/// La clave del diccionario es el Id de la notificacion, que es el Id del evento
/// de origen. Kafka garantiza entrega "al menos una vez", asi que reprocesar es
/// normal: tras un rebalanceo, el consumidor que hereda una particion reanuda
/// desde el ultimo offset CONFIRMADO y puede volver a ver mensajes ya
/// procesados. Sin esta deduplicacion el cliente veria avisos repetidos.
/// </summary>
public sealed class InMemoryNotificationStore : INotificationStore
{
    private readonly ConcurrentDictionary<Guid, Notification> _notifications = new();

    public Task<bool> AddAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        return Task.FromResult(_notifications.TryAdd(notification.Id, notification));
    }

    public Task<IReadOnlyList<Notification>> ListAsync(Guid? customerId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Notification> result = _notifications.Values
            .Where(n => customerId is null || n.CustomerId == customerId)
            .OrderByDescending(n => n.CreatedAt)
            .ToList();

        return Task.FromResult(result);
    }
}

/// <summary>
/// Traduce un evento de integracion al texto que vera el cliente.
/// Funcion pura: entra un evento, sale una notificacion o null si no interesa.
/// </summary>
public static class NotificationFactory
{
    public static Notification? From(IIntegrationEvent integrationEvent)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        return integrationEvent switch
        {
            RentalRequestedIntegrationEvent e => Build(
                e,
                e.RentalId,
                e.CustomerId,
                $"We received your rental request for {e.EstimatedTotal:0.00} {e.Currency}."),

            RentalConfirmedIntegrationEvent e => Build(
                e,
                e.RentalId,
                e.CustomerId,
                $"Your rental is confirmed. Total: {e.EstimatedTotal:0.00} {e.Currency}."),

            RentalCancelledIntegrationEvent e => Build(
                e,
                e.RentalId,
                e.CustomerId,
                $"Your rental was cancelled. Refund: {e.RefundAmount:0.00} {e.Currency} ({e.RefundPercentage:0}%)."),

            RentalStartedIntegrationEvent e => Build(
                e,
                e.RentalId,
                e.CustomerId,
                "Enjoy your trip! The vehicle has been picked up."),

            RentalCompletedIntegrationEvent e => Build(
                e,
                e.RentalId,
                e.CustomerId,
                e.LateDays > 0
                    ? $"Rental completed with {e.LateDays} late day(s). Final total: {e.FinalTotal:0.00} {e.Currency}."
                    : $"Rental completed on time. Final total: {e.FinalTotal:0.00} {e.Currency}."),

            _ => null
        };
    }

    /// <summary>
    /// El Id de la notificacion es el Id del evento. Al ser estable entre
    /// reprocesos, permite que el almacen descarte el duplicado.
    /// </summary>
    private static Notification Build(
        IIntegrationEvent source,
        Guid rentalId,
        Guid customerId,
        string message) =>
        new(source.EventId, rentalId, customerId, source.EventType, message, source.OccurredAt);
}

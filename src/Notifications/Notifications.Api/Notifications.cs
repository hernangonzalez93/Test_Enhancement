using System.Collections.Concurrent;
using Shared.Contracts;

namespace Notifications.Api;

public sealed record Notification(
    Guid Id,
    Guid RentalId,
    Guid CustomerId,
    string EventType,
    string Message,
    DateTimeOffset CreatedAt);

public interface INotificationStore
{
    Task AddAsync(Notification notification, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Notification>> ListAsync(Guid? customerId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Almacen en memoria: Notifications es deliberadamente el servicio mas simple
/// del sistema, para que las pruebas se concentren en el flujo de eventos y no
/// en su persistencia.
/// </summary>
public sealed class InMemoryNotificationStore : INotificationStore
{
    private readonly ConcurrentQueue<Notification> _notifications = new();

    public Task AddAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        _notifications.Enqueue(notification);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Notification>> ListAsync(Guid? customerId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Notification> result = _notifications
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
                e.RentalId,
                e.CustomerId,
                e.EventType,
                $"We received your rental request for {e.EstimatedTotal:0.00} {e.Currency}.",
                e.OccurredAt),

            RentalConfirmedIntegrationEvent e => Build(
                e.RentalId,
                e.CustomerId,
                e.EventType,
                $"Your rental is confirmed. Total: {e.EstimatedTotal:0.00} {e.Currency}.",
                e.OccurredAt),

            RentalCancelledIntegrationEvent e => Build(
                e.RentalId,
                e.CustomerId,
                e.EventType,
                $"Your rental was cancelled. Refund: {e.RefundAmount:0.00} {e.Currency} ({e.RefundPercentage:0}%).",
                e.OccurredAt),

            RentalStartedIntegrationEvent e => Build(
                e.RentalId,
                e.CustomerId,
                e.EventType,
                "Enjoy your trip! The vehicle has been picked up.",
                e.OccurredAt),

            RentalCompletedIntegrationEvent e => Build(
                e.RentalId,
                e.CustomerId,
                e.EventType,
                e.LateDays > 0
                    ? $"Rental completed with {e.LateDays} late day(s). Final total: {e.FinalTotal:0.00} {e.Currency}."
                    : $"Rental completed on time. Final total: {e.FinalTotal:0.00} {e.Currency}.",
                e.OccurredAt),

            _ => null
        };
    }

    private static Notification Build(
        Guid rentalId,
        Guid customerId,
        string eventType,
        string message,
        DateTimeOffset occurredAt) =>
        new(Guid.CreateVersion7(), rentalId, customerId, eventType, message, occurredAt);
}

using Shared.Contracts;

namespace Notifications.Api;

public interface INotificationIngestor
{
    /// <summary>Devuelve true si el evento genero una notificacion.</summary>
    Task<bool> IngestAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default);
}

/// <summary>
/// Separa el "que hacer con el evento" del "como llegan los eventos".
/// Gracias a eso la regla se prueba con dobles y sin broker, y el consumidor
/// de Kafka queda reducido a fontaneria verificable por integracion.
/// </summary>
public sealed class NotificationIngestor(INotificationStore store, ILogger<NotificationIngestor> logger)
    : INotificationIngestor
{
    public async Task<bool> IngestAsync(
        IIntegrationEvent integrationEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var notification = NotificationFactory.From(integrationEvent);
        if (notification is null)
        {
            logger.LogDebug("Event {EventType} does not produce a notification.", integrationEvent.EventType);
            return false;
        }

        var stored = await store.AddAsync(notification, cancellationToken);
        if (!stored)
        {
            // Idempotencia: Kafka entrega "al menos una vez". Tras un rebalanceo,
            // el consumidor que hereda una particion reanuda desde el ultimo
            // offset CONFIRMADO, no desde el ultimo procesado, asi que reprocesar
            // es normal y no debe generar un aviso duplicado al cliente.
            logger.LogDebug("Event {EventId} was already ingested, skipped.", notification.Id);
            return false;
        }

        logger.LogInformation("Stored notification for rental {RentalId}.", notification.RentalId);

        return true;
    }
}

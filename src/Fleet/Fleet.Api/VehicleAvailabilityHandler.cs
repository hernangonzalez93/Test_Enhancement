using Microsoft.EntityFrameworkCore;
using Shared.Contracts;

namespace Fleet.Api;

public interface IVehicleAvailabilityHandler
{
    Task<bool> HandleAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reaccion de Fleet a los eventos de Rentals. Se aisla del consumidor de Kafka
/// a proposito: asi la regla ("confirmar bloquea el vehiculo, cancelar o
/// completar lo libera") se prueba sin levantar un broker, y el consumidor solo
/// necesita una prueba de integracion que verifique el cableado.
/// </summary>
public sealed class VehicleAvailabilityHandler(FleetDbContext context, ILogger<VehicleAvailabilityHandler> logger)
    : IVehicleAvailabilityHandler
{
    public async Task<bool> HandleAsync(
        IIntegrationEvent integrationEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var (vehicleId, available) = integrationEvent switch
        {
            RentalConfirmedIntegrationEvent e => (e.VehicleId, (bool?)false),
            RentalStartedIntegrationEvent e => (e.VehicleId, (bool?)false),
            RentalCancelledIntegrationEvent e => (e.VehicleId, (bool?)true),
            RentalCompletedIntegrationEvent e => (e.VehicleId, (bool?)true),
            _ => (Guid.Empty, null)
        };

        if (available is null)
        {
            logger.LogDebug("Ignoring event {EventType}.", integrationEvent.EventType);
            return false;
        }

        var vehicle = await context.Vehicles.FirstOrDefaultAsync(v => v.Id == vehicleId, cancellationToken);
        if (vehicle is null)
        {
            logger.LogWarning("Received {EventType} for unknown vehicle {VehicleId}.", integrationEvent.EventType, vehicleId);
            return false;
        }

        if (vehicle.Available == available.Value)
        {
            // Idempotencia: reprocesar el mismo evento no debe cambiar nada.
            return false;
        }

        vehicle.Available = available.Value;
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Vehicle {VehicleId} availability set to {Available} after {EventType}.",
            vehicleId,
            available.Value,
            integrationEvent.EventType);

        return true;
    }
}

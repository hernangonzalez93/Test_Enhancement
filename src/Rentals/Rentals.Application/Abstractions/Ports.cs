using Rentals.Domain.Model;
using Shared.Contracts;

namespace Rentals.Application.Abstractions;

/// <summary>
/// Puerto de tiempo. Sin esto, cualquier prueba sobre fechas seria no determinista:
/// es la dependencia que mas dolor evita en toda la arquitectura.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Puerto de persistencia del agregado Rental.</summary>
public interface IRentalRepository
{
    Task<Rental?> GetByIdAsync(RentalId id, CancellationToken cancellationToken = default);

    Task AddAsync(Rental rental, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Rental>> ListByCustomerAsync(CustomerId customerId, CancellationToken cancellationToken = default);

    /// <summary>Existe otra renta viva (Pending/Confirmed/Active) que pise este periodo.</summary>
    Task<bool> HasOverlappingRentalAsync(
        VehicleId vehicleId,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken = default);
}

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Puerto hacia el servicio Fleet (catalogo de vehiculos).</summary>
public interface IVehicleCatalog
{
    Task<VehicleSnapshot?> FindAsync(VehicleId vehicleId, CancellationToken cancellationToken = default);
}

public sealed record VehicleSnapshot(
    Guid Id,
    string Model,
    string VehicleClass,
    decimal BaseDailyRate,
    string Currency,
    bool Available);

/// <summary>Puerto hacia el servicio Pricing (calculo de tarifa).</summary>
public interface IPricingCalculator
{
    Task<PricingQuote> QuoteAsync(PricingRequest request, CancellationToken cancellationToken = default);
}

public sealed record PricingRequest(
    string VehicleClass,
    decimal BaseDailyRate,
    int Days,
    IReadOnlyList<string> Extras,
    string Currency);

public sealed record PricingQuote(
    decimal DailyRate,
    decimal Total,
    string Currency,
    IReadOnlyList<PricingLine> Breakdown);

public sealed record PricingLine(string Concept, decimal Amount);

/// <summary>Puerto de publicacion hacia el bus de eventos (Kafka en produccion).</summary>
public interface IEventPublisher
{
    Task PublishAsync(IReadOnlyCollection<IIntegrationEvent> events, CancellationToken cancellationToken = default);
}

/// <summary>
/// Fallo tecnico de un servicio externo. Se distingue de las excepciones de
/// dominio para que la API responda 503 y no 400.
/// </summary>
public sealed class ExternalServiceUnavailableException(string serviceName, Exception? inner = null)
    : Exception("The external service '" + serviceName + "' is unavailable.", inner)
{
    public string ServiceName { get; } = serviceName;
}

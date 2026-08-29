using Microsoft.EntityFrameworkCore;
using Rentals.Application.Abstractions;
using Rentals.Domain.Model;

namespace Rentals.Infrastructure.Persistence;

/// <summary>
/// Adaptador de salida sobre EF Core. Es la unica clase que sabe que existe
/// PostgreSQL; el dominio y la aplicacion solo conocen <see cref="IRentalRepository"/>.
/// </summary>
public sealed class EfRentalRepository(RentalsDbContext context) : IRentalRepository
{
    public async Task<Rental?> GetByIdAsync(RentalId id, CancellationToken cancellationToken = default) =>
        await context.Rentals.FirstOrDefaultAsync(rental => rental.Id == id, cancellationToken);

    public async Task AddAsync(Rental rental, CancellationToken cancellationToken = default) =>
        await context.Rentals.AddAsync(rental, cancellationToken);

    public async Task<IReadOnlyList<Rental>> ListByCustomerAsync(
        CustomerId customerId,
        CancellationToken cancellationToken = default) =>
        await context.Rentals
            .Where(rental => rental.CustomerId == customerId)
            .OrderByDescending(rental => rental.RequestedAt)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Solapamiento sobre intervalos semiabiertos: dos rentas chocan si
    /// start &lt; other.End y other.Start &lt; end. Las rentas canceladas y
    /// completadas no bloquean el vehiculo.
    /// </summary>
    public async Task<bool> HasOverlappingRentalAsync(
        VehicleId vehicleId,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken = default)
    {
        var blockingStates = new[] { RentalStatus.Pending, RentalStatus.Confirmed, RentalStatus.Active };

        return await context.Rentals.AnyAsync(
            rental => rental.VehicleId == vehicleId
                      && blockingStates.Contains(rental.Status)
                      && rental.Period.Start < end
                      && start < rental.Period.End,
            cancellationToken);
    }
}

/// <summary>
/// Unidad de trabajo. Al compartir el mismo DbContext que el repositorio,
/// todos los cambios de un caso de uso se confirman en una sola transaccion.
/// </summary>
public sealed class EfUnitOfWork(RentalsDbContext context) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);
}

using System.Collections.Concurrent;

namespace Insurances.Api;

/// <summary>
/// Ciclo de vida de una poliza, atado al de la renta que la origina.
/// Draft -> Active -> Expired, con salida a Cancelled desde Draft y Active.
/// </summary>
public enum PolicyStatus
{
    Draft = 0,
    Active = 1,
    Cancelled = 2,
    Expired = 3
}

/// <summary>
/// La identidad de la poliza es la renta: hay como mucho una por renta. Esa
/// eleccion es lo que hace idempotente el consumo de eventos, igual que en Fleet
/// la identidad es el vehiculo.
/// </summary>
public sealed record Policy(
    string Number,
    Guid RentalId,
    Guid CustomerId,
    string Coverage,
    decimal Premium,
    string Currency,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidTo,
    PolicyStatus Status,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// Numero derivado del id de renta y por tanto estable entre reprocesos:
    /// reprocesar un evento no genera una poliza con otro numero.
    /// </summary>
    public static string NumberFor(Guid rentalId) =>
        "POL-" + rentalId.ToString("N")[..8].ToUpperInvariant();
}

public interface IPolicyStore
{
    Task<Policy?> FindAsync(Guid rentalId, CancellationToken cancellationToken = default);

    Task SaveAsync(Policy policy, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Policy>> ListAsync(
        Guid? customerId = null,
        Guid? rentalId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Almacen en memoria indexado por renta. Como en Notifications, la persistencia
/// no es el objeto de estudio: lo es el flujo de eventos.
/// </summary>
public sealed class InMemoryPolicyStore : IPolicyStore
{
    private readonly ConcurrentDictionary<Guid, Policy> _policies = new();

    public Task<Policy?> FindAsync(Guid rentalId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_policies.TryGetValue(rentalId, out var policy) ? policy : null);

    public Task SaveAsync(Policy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policies[policy.RentalId] = policy;

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Policy>> ListAsync(
        Guid? customerId = null,
        Guid? rentalId = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Policy> result = _policies.Values
            .Where(p => customerId is null || p.CustomerId == customerId)
            .Where(p => rentalId is null || p.RentalId == rentalId)
            .OrderByDescending(p => p.UpdatedAt)
            .ToList();

        return Task.FromResult(result);
    }
}

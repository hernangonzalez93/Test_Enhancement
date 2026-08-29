using Shared.Contracts;

namespace Insurances.Api;

public interface IPolicyIssuer
{
    /// <summary>Devuelve true si el evento cambio algo en el almacen de polizas.</summary>
    Task<bool> HandleAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reaccion de Insurances a los eventos de Rentals. Igual que en Fleet, la regla
/// vive aqui y el consumidor de Kafka solo traduce bytes: asi se prueba sin
/// broker y el consumidor queda reducido a fonteneria.
///
/// Todas las transiciones son idempotentes por comparacion contra el estado
/// actual, porque Kafka entrega "al menos una vez".
/// </summary>
public sealed class PolicyIssuer(IPolicyStore store, ILogger<PolicyIssuer> logger) : IPolicyIssuer
{
    public async Task<bool> HandleAsync(
        IIntegrationEvent integrationEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        return integrationEvent switch
        {
            RentalRequestedIntegrationEvent e => await DraftAsync(e, cancellationToken),
            RentalConfirmedIntegrationEvent e => await TransitionAsync(e.RentalId, PolicyStatus.Active, e.OccurredAt, cancellationToken),
            RentalCancelledIntegrationEvent e => await TransitionAsync(e.RentalId, PolicyStatus.Cancelled, e.OccurredAt, cancellationToken),
            RentalCompletedIntegrationEvent e => await TransitionAsync(e.RentalId, PolicyStatus.Expired, e.OccurredAt, cancellationToken),
            RentalExtendedIntegrationEvent e => await ExtendAsync(e, cancellationToken),
            _ => Ignore(integrationEvent)
        };
    }

    /// <summary>La solicitud de renta crea la poliza en borrador, aun sin efecto.</summary>
    private async Task<bool> DraftAsync(RentalRequestedIntegrationEvent e, CancellationToken cancellationToken)
    {
        if (await store.FindAsync(e.RentalId, cancellationToken) is not null)
        {
            return false;
        }

        var days = BillableDays(e.PeriodStart, e.PeriodEnd);
        var quote = PremiumEngine.Quote(
            new PremiumRequest(PremiumEngine.DefaultCoverage, days, e.EstimatedTotal, e.Currency));

        var policy = new Policy(
            Policy.NumberFor(e.RentalId),
            e.RentalId,
            e.CustomerId,
            quote.Coverage,
            quote.Premium,
            quote.Currency,
            e.PeriodStart,
            e.PeriodEnd,
            PolicyStatus.Draft,
            e.OccurredAt);

        await store.SaveAsync(policy, cancellationToken);
        logger.LogInformation("Drafted policy {Number} for rental {RentalId}.", policy.Number, e.RentalId);

        return true;
    }

    private async Task<bool> TransitionAsync(
        Guid rentalId,
        PolicyStatus target,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var policy = await store.FindAsync(rentalId, cancellationToken);
        if (policy is null)
        {
            logger.LogWarning("No policy for rental {RentalId}; event ignored.", rentalId);
            return false;
        }

        if (policy.Status == target || !CanTransition(policy.Status, target))
        {
            // Idempotencia y estados terminales: reprocesar no cambia nada.
            return false;
        }

        await store.SaveAsync(policy with { Status = target, UpdatedAt = occurredAt }, cancellationToken);
        logger.LogInformation("Policy {Number} moved to {Status}.", policy.Number, target);

        return true;
    }

    /// <summary>
    /// Prorrogar la renta alarga la vigencia y recalcula la prima. Sin esto, la
    /// poliza caducaria antes que la renta que asegura.
    /// </summary>
    private async Task<bool> ExtendAsync(RentalExtendedIntegrationEvent e, CancellationToken cancellationToken)
    {
        var policy = await store.FindAsync(e.RentalId, cancellationToken);
        if (policy is null)
        {
            logger.LogWarning("No policy for rental {RentalId}; extension ignored.", e.RentalId);
            return false;
        }

        if (policy.Status is PolicyStatus.Cancelled or PolicyStatus.Expired || policy.ValidTo >= e.NewPeriodEnd)
        {
            return false;
        }

        var days = BillableDays(policy.ValidFrom, e.NewPeriodEnd);
        var quote = PremiumEngine.Quote(
            new PremiumRequest(policy.Coverage, days, e.NewEstimatedTotal, e.Currency));

        await store.SaveAsync(
            policy with
            {
                ValidTo = e.NewPeriodEnd,
                Premium = quote.Premium,
                UpdatedAt = e.OccurredAt
            },
            cancellationToken);

        logger.LogInformation("Policy {Number} extended to {ValidTo}.", policy.Number, e.NewPeriodEnd);

        return true;
    }

    private bool Ignore(IIntegrationEvent integrationEvent)
    {
        logger.LogDebug("Event {EventType} does not affect policies.", integrationEvent.EventType);
        return false;
    }

    private static bool CanTransition(PolicyStatus current, PolicyStatus target) => (current, target) switch
    {
        (PolicyStatus.Draft, PolicyStatus.Active) => true,
        (PolicyStatus.Draft, PolicyStatus.Cancelled) => true,
        (PolicyStatus.Active, PolicyStatus.Cancelled) => true,
        (PolicyStatus.Active, PolicyStatus.Expired) => true,
        _ => false
    };

    /// <summary>Mismo criterio que el dominio de Rentals: cada fraccion de 24 h cuenta.</summary>
    private static int BillableDays(DateTimeOffset start, DateTimeOffset end) =>
        Math.Max(1, (int)Math.Ceiling((end - start).TotalHours / 24d));
}

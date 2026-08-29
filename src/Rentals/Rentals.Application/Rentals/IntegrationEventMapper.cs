using Rentals.Domain.Common;
using Rentals.Domain.Events;
using Shared.Contracts;

namespace Rentals.Application.Rentals;

/// <summary>
/// Traduce eventos de dominio (lenguaje interno) a eventos de integracion
/// (contrato publico). Mantener las dos familias separadas permite refactorizar
/// el dominio sin romper a los consumidores de Kafka.
/// </summary>
public static class IntegrationEventMapper
{
    public static IReadOnlyCollection<IIntegrationEvent> Map(IEnumerable<IDomainEvent> domainEvents) =>
        domainEvents.Select(Map).OfType<IIntegrationEvent>().ToList();

    public static IIntegrationEvent? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        RentalRequested e => new RentalRequestedIntegrationEvent(
            e.RentalId.Value,
            e.CustomerId.Value,
            e.VehicleId.Value,
            e.PeriodStart,
            e.PeriodEnd,
            e.EstimatedTotal.Amount,
            e.EstimatedTotal.Currency,
            e.OccurredAt),

        RentalConfirmed e => new RentalConfirmedIntegrationEvent(
            e.RentalId.Value,
            e.CustomerId.Value,
            e.VehicleId.Value,
            e.EstimatedTotal.Amount,
            e.EstimatedTotal.Currency,
            e.OccurredAt),

        RentalCancelled e => new RentalCancelledIntegrationEvent(
            e.RentalId.Value,
            e.CustomerId.Value,
            e.VehicleId.Value,
            e.EstimatedTotal.Amount,
            e.RefundAmount.Amount,
            e.RefundPercentage,
            e.RefundAmount.Currency,
            e.OccurredAt),

        RentalStarted e => new RentalStartedIntegrationEvent(
            e.RentalId.Value,
            e.CustomerId.Value,
            e.VehicleId.Value,
            e.PickedUpAt,
            e.OccurredAt),

        RentalCompleted e => new RentalCompletedIntegrationEvent(
            e.RentalId.Value,
            e.CustomerId.Value,
            e.VehicleId.Value,
            e.FinalTotal.Amount,
            e.LateDays,
            e.FinalTotal.Currency,
            e.OccurredAt),

        RentalExtended e => new RentalExtendedIntegrationEvent(
            e.RentalId.Value,
            e.CustomerId.Value,
            e.VehicleId.Value,
            e.NewEnd,
            e.NewEstimatedTotal.Amount,
            e.NewEstimatedTotal.Currency,
            e.OccurredAt),

        _ => null
    };
}

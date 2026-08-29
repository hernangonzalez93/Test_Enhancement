using Rentals.Domain.Common;
using Rentals.Domain.Model;

namespace Rentals.Domain.Events;

/// <summary>
/// Eventos de dominio del agregado Rental. Son inmutables, se nombran en pasado
/// y solo llevan datos primitivos o value objects: nunca la entidad completa.
/// </summary>
public sealed record RentalRequested(
    RentalId RentalId,
    CustomerId CustomerId,
    VehicleId VehicleId,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    Money EstimatedTotal,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record RentalConfirmed(
    RentalId RentalId,
    CustomerId CustomerId,
    VehicleId VehicleId,
    Money EstimatedTotal,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record RentalCancelled(
    RentalId RentalId,
    CustomerId CustomerId,
    VehicleId VehicleId,
    Money RefundAmount,
    decimal RefundPercentage,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record RentalStarted(
    RentalId RentalId,
    CustomerId CustomerId,
    VehicleId VehicleId,
    DateTimeOffset PickedUpAt,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record RentalCompleted(
    RentalId RentalId,
    CustomerId CustomerId,
    VehicleId VehicleId,
    Money FinalTotal,
    int LateDays,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record RentalExtended(
    RentalId RentalId,
    CustomerId CustomerId,
    DateTimeOffset NewEnd,
    Money NewEstimatedTotal,
    DateTimeOffset OccurredAt) : IDomainEvent;

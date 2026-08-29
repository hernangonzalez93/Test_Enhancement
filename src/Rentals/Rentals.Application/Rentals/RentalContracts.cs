using Rentals.Domain.Model;

namespace Rentals.Application.Rentals;

public sealed record RequestRentalCommand(
    Guid CustomerId,
    Guid VehicleId,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    string LicenseNumber,
    DateTimeOffset LicenseExpiresOn,
    IReadOnlyList<string>? Extras = null);

public sealed record RentalDto(
    Guid Id,
    Guid CustomerId,
    Guid VehicleId,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    int TotalDays,
    string Status,
    decimal DailyRate,
    decimal EstimatedTotal,
    decimal? FinalTotal,
    decimal? RefundAmount,
    string Currency,
    int LateDays)
{
    public static RentalDto From(Rental rental) => new(
        rental.Id.Value,
        rental.CustomerId.Value,
        rental.VehicleId.Value,
        rental.Period.Start,
        rental.Period.End,
        rental.Period.TotalDays,
        rental.Status.ToString(),
        rental.DailyRate.Amount,
        rental.EstimatedTotal.Amount,
        rental.FinalTotal?.Amount,
        rental.RefundAmount?.Amount,
        rental.EstimatedTotal.Currency,
        rental.LateDays);
}

using Rentals.Domain.Common;
using Rentals.Domain.Events;
using Rentals.Domain.Exceptions;

namespace Rentals.Domain.Model;

/// <summary>
/// Raiz del agregado. Concentra TODAS las reglas de negocio de una renta:
/// que transiciones son legales, cuanto cuesta, cuanto se reembolsa y que
/// recargo aplica por devolucion tardia. No conoce base de datos, HTTP ni Kafka.
/// </summary>
public sealed class Rental : AggregateRoot<RentalId>
{
    // Requerido por EF Core para materializar sin pasar por las reglas de negocio.
    private Rental() : base(default)
    {
        Period = null!;
        License = null!;
        DailyRate = null!;
        EstimatedTotal = null!;
    }

    private Rental(
        RentalId id,
        CustomerId customerId,
        VehicleId vehicleId,
        RentalPeriod period,
        DriverLicense license,
        Money dailyRate,
        Money estimatedTotal,
        DateTimeOffset requestedAt)
        : base(id)
    {
        CustomerId = customerId;
        VehicleId = vehicleId;
        Period = period;
        License = license;
        DailyRate = dailyRate;
        EstimatedTotal = estimatedTotal;
        RequestedAt = requestedAt;
        Status = RentalStatus.Pending;
    }

    public CustomerId CustomerId { get; private set; }

    public VehicleId VehicleId { get; private set; }

    public RentalPeriod Period { get; private set; }

    public DriverLicense License { get; private set; }

    public Money DailyRate { get; private set; }

    public Money EstimatedTotal { get; private set; }

    public Money? FinalTotal { get; private set; }

    public Money? RefundAmount { get; private set; }

    public RentalStatus Status { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset? ConfirmedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public DateTimeOffset? ReturnedAt { get; private set; }

    public int LateDays { get; private set; }

    /// <summary>
    /// Unica via para crear una renta. El precio estimado se calcula aqui a
    /// partir de la tarifa diaria: el dominio nunca confia en un total externo.
    /// </summary>
    public static Rental Request(
        RentalId id,
        CustomerId customerId,
        VehicleId vehicleId,
        RentalPeriod period,
        DriverLicense license,
        Money dailyRate,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(license);
        ArgumentNullException.ThrowIfNull(dailyRate);

        if (period.IsInThePast(now))
        {
            throw new RentalPeriodInThePastException(period.Start, now);
        }

        if (!license.CoversPeriod(period))
        {
            throw new DriverLicenseExpiredException(license.ExpiresOn, period.End);
        }

        var estimatedTotal = dailyRate.Multiply(period.TotalDays);

        var rental = new Rental(id, customerId, vehicleId, period, license, dailyRate, estimatedTotal, now.ToUniversalTime());
        rental.Raise(new RentalRequested(
            id,
            customerId,
            vehicleId,
            period.Start,
            period.End,
            estimatedTotal,
            now.ToUniversalTime()));

        return rental;
    }

    public void Confirm(DateTimeOffset now)
    {
        EnsureStatusIs(RentalStatus.Pending, "confirm");

        Status = RentalStatus.Confirmed;
        ConfirmedAt = now.ToUniversalTime();
        Raise(new RentalConfirmed(Id, CustomerId, VehicleId, EstimatedTotal, ConfirmedAt.Value));
    }

    /// <summary>
    /// Solo se puede cancelar antes de retirar el vehiculo. El reembolso lo
    /// decide <see cref="CancellationPolicy"/> segun la antelacion.
    /// </summary>
    public void Cancel(DateTimeOffset now)
    {
        if (Status is not (RentalStatus.Pending or RentalStatus.Confirmed))
        {
            throw new InvalidRentalStateException(Status.ToString(), "cancel");
        }

        var utcNow = now.ToUniversalTime();
        var percentage = CancellationPolicy.RefundPercentageFor(Period.Start, utcNow);
        var refund = Status == RentalStatus.Pending
            ? Money.Zero(EstimatedTotal.Currency)
            : EstimatedTotal.Percentage(percentage);

        Status = RentalStatus.Cancelled;
        CancelledAt = utcNow;
        RefundAmount = refund;

        Raise(new RentalCancelled(Id, CustomerId, VehicleId, refund, percentage, utcNow));
    }

    /// <summary>Retiro del vehiculo. No se puede retirar antes de la hora pactada.</summary>
    public void Start(DateTimeOffset now)
    {
        EnsureStatusIs(RentalStatus.Confirmed, "start");

        var utcNow = now.ToUniversalTime();
        if (utcNow < Period.Start)
        {
            throw new RentalNotStartableYetException(Period.Start, utcNow);
        }

        Status = RentalStatus.Active;
        StartedAt = utcNow;
        Raise(new RentalStarted(Id, CustomerId, VehicleId, utcNow, utcNow));
    }

    /// <summary>
    /// Devolucion. Cada bloque de 24 horas iniciado despues del fin pactado
    /// se cobra como un dia adicional a tarifa plena.
    /// </summary>
    public void Complete(DateTimeOffset returnedAt)
    {
        EnsureStatusIs(RentalStatus.Active, "complete");

        var utcReturn = returnedAt.ToUniversalTime();
        var lateDays = utcReturn <= Period.End
            ? 0
            : (int)Math.Ceiling((utcReturn - Period.End).TotalHours / 24d);

        var surcharge = DailyRate.Multiply(lateDays);
        var finalTotal = EstimatedTotal.Add(surcharge);

        Status = RentalStatus.Completed;
        ReturnedAt = utcReturn;
        CompletedAt = utcReturn;
        LateDays = lateDays;
        FinalTotal = finalTotal;

        Raise(new RentalCompleted(Id, CustomerId, VehicleId, finalTotal, lateDays, utcReturn));
    }

    /// <summary>Prorroga: solo antes de devolver el vehiculo, y recalcula el estimado.</summary>
    public void Extend(DateTimeOffset newEnd, DateTimeOffset now)
    {
        if (Status is not (RentalStatus.Pending or RentalStatus.Confirmed or RentalStatus.Active))
        {
            throw new InvalidRentalStateException(Status.ToString(), "extend");
        }

        // Se valida sobre el periodo candidato ANTES de mutar el agregado: si la
        // regla falla, la renta queda exactamente como estaba.
        var extended = Period.ExtendTo(newEnd);
        if (!License.CoversPeriod(extended))
        {
            throw new DriverLicenseExpiredException(License.ExpiresOn, extended.End);
        }

        Period = extended;
        EstimatedTotal = DailyRate.Multiply(Period.TotalDays);

        Raise(new RentalExtended(Id, CustomerId, Period.End, EstimatedTotal, now.ToUniversalTime()));
    }

    private void EnsureStatusIs(RentalStatus expected, string transition)
    {
        if (Status != expected)
        {
            throw new InvalidRentalStateException(Status.ToString(), transition);
        }
    }
}

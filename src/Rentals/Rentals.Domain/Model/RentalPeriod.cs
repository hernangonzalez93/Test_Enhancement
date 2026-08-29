using Rentals.Domain.Common;
using Rentals.Domain.Exceptions;

namespace Rentals.Domain.Model;

/// <summary>
/// Intervalo semiabierto [Start, End) normalizado a UTC.
/// La facturacion es por bloques de 24 horas, con un minimo de un dia.
/// </summary>
public sealed class RentalPeriod : ValueObject
{
    public const int MaxDays = 90;

    private RentalPeriod(DateTimeOffset start, DateTimeOffset end)
    {
        Start = start;
        End = end;
    }

    public DateTimeOffset Start { get; }

    public DateTimeOffset End { get; }

    public static RentalPeriod Create(DateTimeOffset start, DateTimeOffset end)
    {
        var utcStart = start.ToUniversalTime();
        var utcEnd = end.ToUniversalTime();

        if (utcEnd <= utcStart)
        {
            throw new InvalidRentalPeriodException("the end must be strictly after the start.");
        }

        var days = CalculateBillableDays(utcStart, utcEnd);
        if (days > MaxDays)
        {
            throw new InvalidRentalPeriodException("a rental cannot exceed " + MaxDays + " days, but got " + days + ".");
        }

        return new RentalPeriod(utcStart, utcEnd);
    }

    private static int CalculateBillableDays(DateTimeOffset start, DateTimeOffset end) =>
        (int)Math.Ceiling((end - start).TotalHours / 24d);

    /// <summary>Dias facturables: cada fraccion de 24 horas cuenta como un dia completo.</summary>
    public int TotalDays => CalculateBillableDays(Start, End);

    public TimeSpan Duration => End - Start;

    public bool Contains(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        return utc >= Start && utc < End;
    }

    /// <summary>Dos periodos se solapan si comparten al menos un instante. Contiguos no se solapan.</summary>
    public bool Overlaps(RentalPeriod other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Start < other.End && other.Start < End;
    }

    public bool IsInThePast(DateTimeOffset now) => Start < now.ToUniversalTime();

    public RentalPeriod ExtendTo(DateTimeOffset newEnd)
    {
        var utcEnd = newEnd.ToUniversalTime();
        if (utcEnd <= End)
        {
            throw new InvalidRentalPeriodException("an extension must move the end date forward.");
        }

        return Create(Start, utcEnd);
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Start;
        yield return End;
    }

    public override string ToString() => Start.ToString("O") + " -> " + End.ToString("O");
}

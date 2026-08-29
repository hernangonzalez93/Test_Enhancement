using Rentals.Application.Abstractions;

namespace TestSupport;

/// <summary>
/// Reloj controlado por la prueba. Sin el, cualquier assert sobre fechas
/// dependeria del instante real de ejecucion y la suite seria intermitente.
/// </summary>
public sealed class FixedClock(DateTimeOffset now) : IClock
{
    /// <summary>Instante de referencia usado por toda la suite: lunes, 10:00 UTC.</summary>
    public static readonly DateTimeOffset DefaultNow = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);

    public FixedClock() : this(DefaultNow)
    {
    }

    public DateTimeOffset UtcNow { get; set; } = now;

    public FixedClock Advance(TimeSpan amount)
    {
        UtcNow = UtcNow.Add(amount);
        return this;
    }

    public FixedClock SetTo(DateTimeOffset instant)
    {
        UtcNow = instant;
        return this;
    }
}

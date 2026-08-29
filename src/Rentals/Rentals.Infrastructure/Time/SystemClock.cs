using Rentals.Application.Abstractions;

namespace Rentals.Infrastructure.Time;

/// <summary>
/// Adaptador trivial pero imprescindible: es el unico punto del sistema que
/// consulta el reloj real. En pruebas se sustituye por un reloj fijo.
/// </summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

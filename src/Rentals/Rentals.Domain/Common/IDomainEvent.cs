namespace Rentals.Domain.Common;

/// <summary>
/// Hecho de negocio que ya ocurrio dentro del dominio. Se nombra en pasado.
/// El dominio los acumula; la capa de aplicacion decide que hacer con ellos.
/// </summary>
public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}

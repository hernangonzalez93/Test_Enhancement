namespace Rentals.Domain.Model;

/// <summary>
/// Maquina de estados de la renta.
/// Pending -> Confirmed -> Active -> Completed, con salida a Cancelled
/// desde Pending y Confirmed unicamente.
/// </summary>
public enum RentalStatus
{
    Pending = 0,
    Confirmed = 1,
    Active = 2,
    Completed = 3,
    Cancelled = 4
}

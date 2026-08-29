namespace Rentals.Domain.Model;

/// <summary>
/// Identificadores fuertemente tipados: impiden pasar un VehicleId donde se espera
/// un CustomerId. Ese es un error que atrapa el compilador, no una prueba.
/// </summary>
public readonly record struct RentalId(Guid Value)
{
    public static RentalId New() => new(Guid.CreateVersion7());

    public static RentalId From(Guid value) => value == Guid.Empty
        ? throw new ArgumentException("RentalId cannot be empty.", nameof(value))
        : new RentalId(value);

    public override string ToString() => Value.ToString();
}

public readonly record struct VehicleId(Guid Value)
{
    public static VehicleId New() => new(Guid.CreateVersion7());

    public static VehicleId From(Guid value) => value == Guid.Empty
        ? throw new ArgumentException("VehicleId cannot be empty.", nameof(value))
        : new VehicleId(value);

    public override string ToString() => Value.ToString();
}

public readonly record struct CustomerId(Guid Value)
{
    public static CustomerId New() => new(Guid.CreateVersion7());

    public static CustomerId From(Guid value) => value == Guid.Empty
        ? throw new ArgumentException("CustomerId cannot be empty.", nameof(value))
        : new CustomerId(value);

    public override string ToString() => Value.ToString();
}

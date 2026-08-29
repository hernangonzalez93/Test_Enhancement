namespace Rentals.Application.Common;

/// <summary>
/// Error de negocio esperado. No es una excepcion: un vehiculo ocupado no es
/// un fallo del sistema, es una respuesta valida del caso de uso.
/// </summary>
public sealed record Error(string Code, string Message)
{
    public static readonly Error None = new(string.Empty, string.Empty);
}

public sealed class Result<T>
{
    private Result(bool isSuccess, T? value, Error error)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public T? Value { get; }

    public Error Error { get; }

    public static Result<T> Success(T value) => new(true, value, Error.None);

    public static Result<T> Failure(Error error) => new(false, default, error);

    public static Result<T> Failure(string code, string message) => Failure(new Error(code, message));
}

/// <summary>Catalogo de errores del caso de uso. Los codigos son parte del contrato de la API.</summary>
public static class RentalErrors
{
    public static Error NotFound(Guid id) =>
        new("rental.not_found", "Rental " + id + " was not found.");

    public static Error VehicleNotFound(Guid id) =>
        new("vehicle.not_found", "Vehicle " + id + " was not found.");

    public static readonly Error VehicleUnavailable =
        new("vehicle.unavailable", "The vehicle is not available for rental.");

    public static readonly Error OverlappingRental =
        new("rental.overlapping", "The vehicle already has a rental overlapping the requested period.");

    public static readonly Error PricingUnavailable =
        new("pricing.unavailable", "The pricing service could not be reached.");

    public static readonly Error FleetUnavailable =
        new("fleet.unavailable", "The fleet service could not be reached.");
}

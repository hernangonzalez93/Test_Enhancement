namespace Rentals.Domain.Exceptions;

/// <summary>
/// Base de todos los errores que representan una regla de negocio rota.
/// Nunca se usa para errores tecnicos: eso permite mapearla a 4xx sin ambiguedad.
/// </summary>
public abstract class DomainException(string message) : Exception(message)
{
    /// <summary>Codigo estable para que la API lo exponga sin filtrar mensajes internos.</summary>
    public abstract string Code { get; }
}

public sealed class NegativeMoneyException(decimal amount)
    : DomainException($"Monetary amounts cannot be negative, but got {amount}.")
{
    public override string Code => "money.negative";
}

public sealed class InvalidCurrencyException(string currency)
    : DomainException($"'{currency}' is not a valid ISO-4217 currency code.")
{
    public override string Code => "money.invalid_currency";
}

public sealed class CurrencyMismatchException(string left, string right)
    : DomainException($"Cannot operate on different currencies: {left} and {right}.")
{
    public override string Code => "money.currency_mismatch";
}

public sealed class InvalidRentalPeriodException(string reason)
    : DomainException($"Invalid rental period: {reason}")
{
    public override string Code => "rental.invalid_period";
}

public sealed class RentalPeriodInThePastException(DateTimeOffset start, DateTimeOffset now)
    : DomainException($"Rental cannot start at {start:O} because it is before now ({now:O}).")
{
    public override string Code => "rental.period_in_the_past";
}

public sealed class InvalidDriverLicenseException(string reason)
    : DomainException($"Invalid driver license: {reason}")
{
    public override string Code => "rental.invalid_license";
}

public sealed class DriverLicenseExpiredException(DateTimeOffset expiresOn, DateTimeOffset periodEnd)
    : DomainException($"Driver license expires on {expiresOn:O}, before the rental ends on {periodEnd:O}.")
{
    public override string Code => "rental.license_expired";
}

public sealed class InvalidRentalStateException(string currentState, string attemptedTransition)
    : DomainException($"Cannot {attemptedTransition} a rental in state '{currentState}'.")
{
    public override string Code => "rental.invalid_state";

    public string CurrentState { get; } = currentState;

    public string AttemptedTransition { get; } = attemptedTransition;
}

public sealed class RentalNotStartableYetException(DateTimeOffset periodStart, DateTimeOffset now)
    : DomainException($"Rental cannot be picked up at {now:O}; it starts at {periodStart:O}.")
{
    public override string Code => "rental.not_startable_yet";
}

using Rentals.Domain.Common;
using Rentals.Domain.Exceptions;

namespace Rentals.Domain.Model;

/// <summary>
/// Importe monetario. Invariantes: nunca negativo, siempre con moneda ISO-4217
/// y redondeado a 2 decimales para que la igualdad estructural sea predecible.
/// </summary>
public sealed class Money : ValueObject, IComparable<Money>
{
    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    public string Currency { get; }

    public static Money Of(decimal amount, string currency)
    {
        if (amount < 0)
        {
            throw new NegativeMoneyException(amount);
        }

        var normalized = NormalizeCurrency(currency);
        return new Money(Math.Round(amount, 2, MidpointRounding.AwayFromZero), normalized);
    }

    public static Money Zero(string currency) => Of(0m, currency);

    private static string NormalizeCurrency(string currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
        {
            throw new InvalidCurrencyException(currency ?? "null");
        }

        var normalized = currency.Trim().ToUpperInvariant();
        if (normalized.Length != 3 || !normalized.All(char.IsAsciiLetterUpper))
        {
            throw new InvalidCurrencyException(currency);
        }

        return normalized;
    }

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return Of(Amount + other.Amount, Currency);
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return Of(Amount - other.Amount, Currency);
    }

    public Money Multiply(decimal factor)
    {
        if (factor < 0)
        {
            throw new NegativeMoneyException(factor);
        }

        return Of(Amount * factor, Currency);
    }

    /// <summary>Porcentaje entre 0 y 100.</summary>
    public Money Percentage(decimal percent) => Multiply(percent / 100m);

    public bool IsZero => Amount == 0m;

    private void EnsureSameCurrency(Money other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!string.Equals(Currency, other.Currency, StringComparison.Ordinal))
        {
            throw new CurrencyMismatchException(Currency, other.Currency);
        }
    }

    public int CompareTo(Money? other)
    {
        if (other is null)
        {
            return 1;
        }

        EnsureSameCurrency(other);
        return Amount.CompareTo(other.Amount);
    }

    public static Money operator +(Money left, Money right) => left.Add(right);

    public static Money operator -(Money left, Money right) => left.Subtract(right);

    public static Money operator *(Money left, decimal factor) => left.Multiply(factor);

    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Amount;
        yield return Currency;
    }

    public override string ToString() => Amount.ToString("0.00") + " " + Currency;
}

namespace Billing.Domain;

/// <summary>
/// Base para objetos de valor: identidad estructural, no referencial.
/// </summary>
public abstract class ValueObject : IEquatable<ValueObject>
{
    protected abstract IEnumerable<object?> GetEqualityComponents();

    public bool Equals(ValueObject? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (GetType() != other.GetType()) return false;
        return GetEqualityComponents().SequenceEqual(other.GetEqualityComponents());
    }

    public override bool Equals(object? obj) => Equals(obj as ValueObject);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var component in GetEqualityComponents())
        {
            hash.Add(component);
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(ValueObject? left, ValueObject? right) => Equals(left, right);

    public static bool operator !=(ValueObject? left, ValueObject? right) => !Equals(left, right);
}

public abstract class BillingDomainException(string message) : Exception(message)
{
    public abstract string Code { get; }
}

public sealed class NegativeAmountException(decimal amount)
    : BillingDomainException($"Monetary amounts cannot be negative, but got {amount}.")
{
    public override string Code => "money.negative";
}

public sealed class InvalidCurrencyException(string currency)
    : BillingDomainException($"'{currency}' is not a valid ISO-4217 currency code.")
{
    public override string Code => "money.invalid_currency";
}

public sealed class CurrencyMismatchException(string left, string right)
    : BillingDomainException($"Cannot operate on different currencies: {left} and {right}.")
{
    public override string Code => "money.currency_mismatch";
}

/// <summary>
/// Money propio del contexto de facturacion.
///
/// Es casi identico al de Rentals, y esa duplicacion es deliberada: son dos
/// contextos delimitados distintos. Compartir el tipo los acoplaria, y el dia
/// que facturacion necesitara redondeo bancario o varias divisas por factura,
/// el cambio arrastraria al dominio de rentas.
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
            throw new NegativeAmountException(amount);
        }

        if (string.IsNullOrWhiteSpace(currency))
        {
            throw new InvalidCurrencyException(currency ?? "null");
        }

        var normalized = currency.Trim().ToUpperInvariant();
        if (normalized.Length != 3 || !normalized.All(char.IsAsciiLetterUpper))
        {
            throw new InvalidCurrencyException(currency);
        }

        return new Money(Math.Round(amount, 2, MidpointRounding.AwayFromZero), normalized);
    }

    public static Money Zero(string currency) => Of(0m, currency);

    public bool IsZero => Amount == 0m;

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

    public Money Percentage(decimal percent) => Of(Amount * percent / 100m, Currency);

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

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Amount;
        yield return Currency;
    }

    public override string ToString() => Amount.ToString("0.00") + " " + Currency;
}

using Rentals.Domain.Exceptions;
using Rentals.Domain.Model;

namespace Rentals.Domain.Tests;

/// <summary>
/// Nivel 1 de la piramide: un value object sin dependencias.
/// Cada prueba nombra la regla, no el metodo, y se ejecuta en microsegundos.
/// </summary>
public sealed class MoneyTests
{
    [Fact]
    public void Of_rounds_to_two_decimals_away_from_zero()
    {
        var money = Money.Of(10.005m, "usd");

        money.Amount.ShouldBe(10.01m);
    }

    [Fact]
    public void Of_normalizes_the_currency_to_uppercase()
    {
        Money.Of(10m, "  usd ").Currency.ShouldBe("USD");
    }

    [Fact]
    public void Of_rejects_negative_amounts()
    {
        var exception = Should.Throw<NegativeMoneyException>(() => Money.Of(-0.01m, "USD"));

        exception.Code.ShouldBe("money.negative");
    }

    [Theory]
    [InlineData("US")]
    [InlineData("USDD")]
    [InlineData("12A")]
    [InlineData("")]
    [InlineData("   ")]
    public void Of_rejects_currencies_that_are_not_three_letters(string currency)
    {
        Should.Throw<InvalidCurrencyException>(() => Money.Of(10m, currency));
    }

    [Fact]
    public void Add_sums_amounts_of_the_same_currency()
    {
        var total = Money.Of(10.50m, "USD").Add(Money.Of(4.50m, "USD"));

        total.Amount.ShouldBe(15.00m);
        total.Currency.ShouldBe("USD");
    }

    [Fact]
    public void Add_rejects_a_different_currency()
    {
        var exception = Should.Throw<CurrencyMismatchException>(
            () => Money.Of(10m, "USD").Add(Money.Of(10m, "EUR")));

        exception.Code.ShouldBe("money.currency_mismatch");
    }

    [Fact]
    public void Subtract_below_zero_is_rejected_because_money_is_never_negative()
    {
        Should.Throw<NegativeMoneyException>(() => Money.Of(10m, "USD").Subtract(Money.Of(11m, "USD")));
    }

    [Theory]
    [InlineData(3, 150)]
    [InlineData(0, 0)]
    [InlineData(1, 50)]
    public void Multiply_scales_the_amount(decimal factor, decimal expected)
    {
        (Money.Of(50m, "USD") * factor).Amount.ShouldBe(expected);
    }

    [Fact]
    public void Multiply_rejects_a_negative_factor()
    {
        Should.Throw<NegativeMoneyException>(() => Money.Of(50m, "USD").Multiply(-1m));
    }

    [Theory]
    [InlineData(100, 200)]
    [InlineData(50, 100)]
    [InlineData(25, 50)]
    [InlineData(0, 0)]
    public void Percentage_applies_the_given_share(decimal percent, decimal expected)
    {
        Money.Of(200m, "USD").Percentage(percent).Amount.ShouldBe(expected);
    }

    [Fact]
    public void Two_amounts_with_the_same_value_are_equal()
    {
        Money.Of(10m, "USD").ShouldBe(Money.Of(10m, "USD"));
        (Money.Of(10m, "USD") == Money.Of(10m, "USD")).ShouldBeTrue();
        Money.Of(10m, "USD").GetHashCode().ShouldBe(Money.Of(10m, "USD").GetHashCode());
    }

    [Fact]
    public void Amounts_with_the_same_number_but_different_currency_are_not_equal()
    {
        // Se comparan con Equals y no con ShouldNotBe a proposito: Shouldly
        // usaria IComparable, y CompareTo lanza ante monedas distintas porque
        // ordenar USD frente a EUR no significa nada en el dominio.
        Money.Of(10m, "USD").Equals(Money.Of(10m, "EUR")).ShouldBeFalse();
        (Money.Of(10m, "USD") == Money.Of(10m, "EUR")).ShouldBeFalse();
    }

    [Fact]
    public void Comparing_two_different_currencies_is_rejected()
    {
        Should.Throw<CurrencyMismatchException>(() => Money.Of(10m, "USD").CompareTo(Money.Of(10m, "EUR")));
    }

    [Fact]
    public void Comparison_operators_order_amounts_of_the_same_currency()
    {
        var cheap = Money.Of(10m, "USD");
        var expensive = Money.Of(20m, "USD");

        (expensive > cheap).ShouldBeTrue();
        (cheap < expensive).ShouldBeTrue();
        (cheap >= Money.Of(10m, "USD")).ShouldBeTrue();
        (cheap <= Money.Of(10m, "USD")).ShouldBeTrue();
    }

    [Fact]
    public void Zero_creates_an_empty_amount_in_the_given_currency()
    {
        var zero = Money.Zero("EUR");

        zero.IsZero.ShouldBeTrue();
        zero.Currency.ShouldBe("EUR");
    }
}

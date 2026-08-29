using Rentals.Domain.Model;
using TestSupport;

namespace Rentals.Domain.Tests;

/// <summary>
/// Caso de libro para [Theory]: una funcion pura con tramos. Una fila por
/// frontera, incluidos los limites exactos, que es donde viven los bugs.
/// </summary>
public sealed class CancellationPolicyTests
{
    private static readonly DateTimeOffset Start = FixedClock.DefaultNow.AddDays(10);

    [Theory]
    [InlineData(240, 100)]  // diez dias antes
    [InlineData(49, 100)]
    [InlineData(48, 100)]   // limite exacto del reembolso total
    [InlineData(47, 50)]
    [InlineData(24, 50)]    // limite exacto del reembolso parcial
    [InlineData(23, 25)]
    [InlineData(2, 25)]     // limite exacto del reembolso minimo
    [InlineData(1, 0)]
    [InlineData(0, 0)]      // justo al comenzar
    [InlineData(-5, 0)]     // ya iniciada
    public void RefundPercentageFor_applies_the_tier_matching_the_notice(int hoursAhead, decimal expected)
    {
        var cancelledAt = Start.AddHours(-hoursAhead);

        CancellationPolicy.RefundPercentageFor(Start, cancelledAt).ShouldBe(expected);
    }

    [Fact]
    public void RefundFor_applies_the_percentage_to_the_total()
    {
        var total = Money.Of(300m, "USD");

        var refund = CancellationPolicy.RefundFor(total, Start, Start.AddHours(-30));

        refund.Amount.ShouldBe(150m);
        refund.Currency.ShouldBe("USD");
    }

    [Fact]
    public void RefundFor_keeps_the_currency_of_the_original_total()
    {
        var refund = CancellationPolicy.RefundFor(Money.Of(100m, "EUR"), Start, Start.AddHours(-72));

        refund.Currency.ShouldBe("EUR");
        refund.Amount.ShouldBe(100m);
    }

    [Fact]
    public void RefundFor_returns_zero_when_the_rental_already_started()
    {
        CancellationPolicy.RefundFor(Money.Of(300m, "USD"), Start, Start.AddHours(1)).IsZero.ShouldBeTrue();
    }
}

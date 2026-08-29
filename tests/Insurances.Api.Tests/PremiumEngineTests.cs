using Insurances.Api;

namespace Insurances.Api.Tests;

/// <summary>
/// Mismo patron que <c>PricingEngineTests</c>: el motor es una funcion pura, asi
/// que se cubre con [Theory] cada tramo y cada frontera.
/// </summary>
public sealed class PremiumEngineTests
{
    private static PremiumRequest Request(
        string coverage = "standard",
        int days = 3,
        decimal rentalTotal = 150m) =>
        new(coverage, days, rentalTotal, "USD");

    [Theory]
    [InlineData("basic", 5)]
    [InlineData("standard", 9)]
    [InlineData("premium", 15)]
    public void The_daily_minimum_applies_when_the_rental_is_cheap(string coverage, decimal dailyMinimum)
    {
        // Renta de importe 0: no hay porcentaje que aplicar, manda el minimo diario.
        var quote = PremiumEngine.Quote(Request(coverage, days: 4, rentalTotal: 0m));

        quote.Premium.ShouldBe(dailyMinimum * 4);
    }

    [Theory]
    [InlineData("basic", 8)]
    [InlineData("standard", 12)]
    [InlineData("premium", 18)]
    public void The_percentage_applies_when_the_rental_is_expensive(string coverage, decimal percentage)
    {
        // Un dia y renta cara: el porcentaje supera con creces el minimo diario.
        var quote = PremiumEngine.Quote(Request(coverage, days: 1, rentalTotal: 1000m));

        quote.Premium.ShouldBe(1000m * percentage / 100m);
    }

    [Fact]
    public void The_premium_is_the_greater_of_the_two_criteria()
    {
        // standard: minimo 9/dia x 10 dias = 90; porcentaje 12% de 500 = 60.
        PremiumEngine.Quote(Request("standard", days: 10, rentalTotal: 500m)).Premium.ShouldBe(90m);

        // standard: minimo 9/dia x 2 dias = 18; porcentaje 12% de 500 = 60.
        PremiumEngine.Quote(Request("standard", days: 2, rentalTotal: 500m)).Premium.ShouldBe(60m);
    }

    [Fact]
    public void The_breakdown_says_which_criterion_gano()
    {
        PremiumEngine.Quote(Request("standard", days: 10, rentalTotal: 500m))
            .Breakdown.ShouldContain(line => line.Concept == "applied:daily-minimum");

        PremiumEngine.Quote(Request("standard", days: 2, rentalTotal: 500m))
            .Breakdown.ShouldContain(line => line.Concept == "applied:percentage");
    }

    [Fact]
    public void The_coverage_is_matched_ignoring_case_and_normalized()
    {
        PremiumEngine.Quote(Request("PREMIUM", days: 1, rentalTotal: 0m)).Coverage.ShouldBe("premium");
    }

    [Fact]
    public void An_unknown_coverage_is_rejected()
    {
        Should.Throw<InsuranceException>(() => PremiumEngine.Quote(Request("golden")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_number_of_days_is_rejected(int days)
    {
        Should.Throw<InsuranceException>(() => PremiumEngine.Quote(Request(days: days)));
    }

    [Fact]
    public void A_negative_rental_total_is_rejected()
    {
        Should.Throw<InsuranceException>(() => PremiumEngine.Quote(Request(rentalTotal: -1m)));
    }

    [Fact]
    public void The_breakdown_exposes_the_excess_of_the_coverage()
    {
        var quote = PremiumEngine.Quote(Request("basic", days: 1, rentalTotal: 0m));

        quote.Breakdown.ShouldContain(line => line.Concept == "excess" && line.Amount == 600m);
    }

    [Fact]
    public void Amounts_are_rounded_to_two_decimals()
    {
        // 12% de 33.33 = 3.9996 -> 4.00
        PremiumEngine.Quote(Request("standard", days: 1, rentalTotal: 33.33m))
            .Breakdown.ShouldContain(line => line.Concept == "percentage:12pct" && line.Amount == 4.00m);
    }

    [Fact]
    public void The_currency_defaults_to_usd_when_missing()
    {
        PremiumEngine.Quote(new PremiumRequest("standard", 1, 100m, "")).Currency.ShouldBe("USD");
    }
}

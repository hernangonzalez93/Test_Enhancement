using Pricing.Api;

namespace Pricing.Api.Tests;

/// <summary>
/// El servicio Pricing es una funcion pura envuelta en HTTP. Toda su
/// complejidad esta en el motor de tarifas, asi que casi toda su cobertura
/// es unitaria y parametrizada.
/// </summary>
public sealed class PricingEngineTests
{
    private static QuoteRequest Request(
        string vehicleClass = "economy",
        decimal baseRate = 30m,
        int days = 3,
        IReadOnlyList<string>? extras = null) =>
        new(vehicleClass, baseRate, days, extras, "USD");

    [Theory]
    [InlineData("economy", 30, 30)]
    [InlineData("compact", 30, 33)]
    [InlineData("suv", 30, 40.5)]
    [InlineData("luxury", 30, 54)]
    public void The_class_multiplier_is_applied_to_the_base_rate(string vehicleClass, decimal baseRate, decimal expectedDaily)
    {
        PricingEngine.Quote(Request(vehicleClass, baseRate, days: 1)).DailyRate.ShouldBe(expectedDaily);
    }

    [Fact]
    public void The_class_is_matched_ignoring_case()
    {
        PricingEngine.Quote(Request("SUV", 30m, 1)).DailyRate.ShouldBe(40.5m);
    }

    [Fact]
    public void An_unknown_class_is_rejected()
    {
        Should.Throw<PricingException>(() => PricingEngine.Quote(Request("spaceship")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_number_of_days_is_rejected(int days)
    {
        Should.Throw<PricingException>(() => PricingEngine.Quote(Request(days: days)));
    }

    [Fact]
    public void A_negative_base_rate_is_rejected()
    {
        Should.Throw<PricingException>(() => PricingEngine.Quote(Request(baseRate: -1m)));
    }

    [Fact]
    public void Extras_are_charged_per_day_on_top_of_the_class_rate()
    {
        var quote = PricingEngine.Quote(Request("economy", 30m, 1, ["gps", "child-seat"]));

        quote.DailyRate.ShouldBe(42m);
        quote.Total.ShouldBe(42m);
    }

    [Fact]
    public void An_unknown_extra_is_rejected()
    {
        Should.Throw<PricingException>(() => PricingEngine.Quote(Request(extras: ["helicopter"])));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(6, 0)]
    [InlineData(7, 0.10)]
    [InlineData(29, 0.10)]
    [InlineData(30, 0.20)]
    [InlineData(90, 0.20)]
    public void The_length_discount_uses_the_documented_thresholds(int days, decimal expected)
    {
        PricingEngine.DiscountFor(days).ShouldBe(expected);
    }

    [Fact]
    public void A_weekly_rental_gets_ten_percent_off_the_gross_amount()
    {
        var quote = PricingEngine.Quote(Request("economy", 30m, 7));

        // 30 * 7 = 210, menos 10% = 189
        quote.Total.ShouldBe(189m);
        quote.Breakdown.ShouldContain(line => line.Concept.StartsWith("discount") && line.Amount == -21m);
    }

    [Fact]
    public void A_short_rental_has_no_discount_line()
    {
        PricingEngine.Quote(Request("economy", 30m, 3)).Breakdown
            .ShouldNotContain(line => line.Concept.StartsWith("discount"));
    }

    [Fact]
    public void The_breakdown_explains_base_class_and_extras()
    {
        var quote = PricingEngine.Quote(Request("suv", 60m, 2, ["insurance"]));

        quote.Breakdown.Select(line => line.Concept).ShouldBe(["base", "class:suv", "extra:insurance"]);
        quote.Breakdown[0].Amount.ShouldBe(60m);
        quote.Breakdown[1].Amount.ShouldBe(21m);
        quote.Breakdown[2].Amount.ShouldBe(15m);
    }

    [Fact]
    public void The_currency_defaults_to_usd_when_missing()
    {
        PricingEngine.Quote(new QuoteRequest("economy", 30m, 1, null, "")).Currency.ShouldBe("USD");
    }

    [Fact]
    public void Amounts_are_rounded_to_two_decimals()
    {
        var quote = PricingEngine.Quote(Request("suv", 33.33m, 1));

        quote.DailyRate.ShouldBe(45m);
    }
}

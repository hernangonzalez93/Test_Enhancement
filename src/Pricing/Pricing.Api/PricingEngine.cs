namespace Pricing.Api;

public sealed record QuoteRequest(
    string VehicleClass,
    decimal BaseDailyRate,
    int Days,
    IReadOnlyList<string>? Extras,
    string Currency);

public sealed record QuoteLine(string Concept, decimal Amount);

public sealed record QuoteResponse(
    decimal DailyRate,
    decimal Total,
    string Currency,
    IReadOnlyList<QuoteLine> Breakdown);

public sealed class PricingException(string message) : Exception(message);

/// <summary>
/// Toda la logica de tarifas del servicio Pricing, sin dependencias.
/// Igual que el dominio de Rentals, es codigo puro: sus pruebas son unitarias,
/// deterministas y cubren cada rama de precio.
/// </summary>
public static class PricingEngine
{
    public static readonly IReadOnlyDictionary<string, decimal> ClassMultipliers =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["economy"] = 1.00m,
            ["compact"] = 1.10m,
            ["suv"] = 1.35m,
            ["luxury"] = 1.80m
        };

    /// <summary>Costo diario de cada extra, en la misma moneda de la tarifa base.</summary>
    public static readonly IReadOnlyDictionary<string, decimal> ExtraDailyPrices =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["gps"] = 5m,
            ["child-seat"] = 7m,
            ["insurance"] = 15m,
            ["additional-driver"] = 10m
        };

    public const decimal WeeklyDiscount = 0.10m;
    public const decimal MonthlyDiscount = 0.20m;

    public static decimal DiscountFor(int days) => days switch
    {
        >= 30 => MonthlyDiscount,
        >= 7 => WeeklyDiscount,
        _ => 0m
    };

    public static QuoteResponse Quote(QuoteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Days <= 0)
        {
            throw new PricingException("Days must be greater than zero.");
        }

        if (request.BaseDailyRate < 0)
        {
            throw new PricingException("Base daily rate cannot be negative.");
        }

        var vehicleClass = request.VehicleClass ?? string.Empty;
        if (!ClassMultipliers.TryGetValue(vehicleClass, out var multiplier))
        {
            throw new PricingException($"Unknown vehicle class '{vehicleClass}'.");
        }

        var lines = new List<QuoteLine>();

        var classRate = Round(request.BaseDailyRate * multiplier);
        lines.Add(new QuoteLine("base", Round(request.BaseDailyRate)));
        lines.Add(new QuoteLine($"class:{vehicleClass.ToLowerInvariant()}", Round(classRate - request.BaseDailyRate)));

        var extrasDaily = 0m;
        foreach (var extra in request.Extras ?? [])
        {
            if (!ExtraDailyPrices.TryGetValue(extra, out var price))
            {
                throw new PricingException($"Unknown extra '{extra}'.");
            }

            extrasDaily += price;
            lines.Add(new QuoteLine($"extra:{extra.ToLowerInvariant()}", price));
        }

        var dailyRate = Round(classRate + extrasDaily);
        var gross = Round(dailyRate * request.Days);

        var discountRate = DiscountFor(request.Days);
        var discount = Round(gross * discountRate);
        if (discount > 0)
        {
            lines.Add(new QuoteLine($"discount:{discountRate * 100:0}pct", -discount));
        }

        var total = Round(gross - discount);

        return new QuoteResponse(dailyRate, total, NormalizeCurrency(request.Currency), lines);
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string NormalizeCurrency(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? "USD" : currency.Trim().ToUpperInvariant();
}

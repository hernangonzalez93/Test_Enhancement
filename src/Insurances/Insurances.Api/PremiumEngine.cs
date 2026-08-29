namespace Insurances.Api;

public sealed record PremiumRequest(
    string Coverage,
    int Days,
    decimal RentalTotal,
    string Currency);

public sealed record PremiumLine(string Concept, decimal Amount);

public sealed record PremiumQuote(
    string Coverage,
    decimal Premium,
    string Currency,
    IReadOnlyList<PremiumLine> Breakdown);

public sealed class InsuranceException(string message) : Exception(message);

/// <summary>Condiciones economicas de un nivel de cobertura.</summary>
public sealed record CoverageTerms(decimal DailyMinimum, decimal PercentageOfRental, decimal Excess);

/// <summary>
/// Toda la logica de calculo de primas, sin dependencias. Igual que
/// <c>PricingEngine</c>, es codigo puro: sus pruebas son unitarias, deterministas
/// y cubren cada rama.
///
/// La regla central es un maximo entre dos criterios, y por eso merece
/// <c>[Theory]</c>: la prima nunca baja de un minimo diario, pero en rentas caras
/// manda el porcentaje sobre el importe.
/// </summary>
public static class PremiumEngine
{
    public const string DefaultCoverage = "standard";

    public static readonly IReadOnlyDictionary<string, CoverageTerms> Coverages =
        new Dictionary<string, CoverageTerms>(StringComparer.OrdinalIgnoreCase)
        {
            ["basic"] = new(DailyMinimum: 5m, PercentageOfRental: 8m, Excess: 600m),
            ["standard"] = new(DailyMinimum: 9m, PercentageOfRental: 12m, Excess: 300m),
            ["premium"] = new(DailyMinimum: 15m, PercentageOfRental: 18m, Excess: 0m)
        };

    public static PremiumQuote Quote(PremiumRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Days <= 0)
        {
            throw new InsuranceException("Days must be greater than zero.");
        }

        if (request.RentalTotal < 0)
        {
            throw new InsuranceException("Rental total cannot be negative.");
        }

        var coverage = request.Coverage ?? string.Empty;
        if (!Coverages.TryGetValue(coverage, out var terms))
        {
            throw new InsuranceException($"Unknown coverage '{coverage}'.");
        }

        var byDays = Round(terms.DailyMinimum * request.Days);
        var byValue = Round(request.RentalTotal * terms.PercentageOfRental / 100m);
        var premium = Math.Max(byDays, byValue);

        var breakdown = new List<PremiumLine>
        {
            new($"minimum:{terms.DailyMinimum:0.##}/day x {request.Days}", byDays),
            new($"percentage:{terms.PercentageOfRental:0}pct", byValue),
            new(byDays >= byValue ? "applied:daily-minimum" : "applied:percentage", premium),
            new("excess", terms.Excess)
        };

        return new PremiumQuote(
            coverage.ToLowerInvariant(),
            premium,
            NormalizeCurrency(request.Currency),
            breakdown);
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string NormalizeCurrency(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? "USD" : currency.Trim().ToUpperInvariant();
}

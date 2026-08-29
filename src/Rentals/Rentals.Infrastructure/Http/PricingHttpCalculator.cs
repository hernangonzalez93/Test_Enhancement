using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Rentals.Application.Abstractions;

namespace Rentals.Infrastructure.Http;

/// <summary>
/// Adaptador HTTP hacia el servicio Pricing. A diferencia de Fleet, aqui no
/// existe el caso "no encontrado": o hay tarifa o el servicio esta caido.
/// </summary>
public sealed class PricingHttpCalculator(HttpClient httpClient, ILogger<PricingHttpCalculator> logger)
    : IPricingCalculator
{
    public const string HttpClientName = "pricing";

    public async Task<PricingQuote> QuoteAsync(
        PricingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "/api/quotes",
                new PricingQuoteRequest(
                    request.VehicleClass,
                    request.BaseDailyRate,
                    request.Days,
                    request.Extras,
                    request.Currency),
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Pricing returned {StatusCode}.", response.StatusCode);
                throw new ExternalServiceUnavailableException("pricing");
            }

            var payload = await response.Content.ReadFromJsonAsync<PricingQuoteResponse>(cancellationToken)
                          ?? throw new ExternalServiceUnavailableException("pricing");

            return new PricingQuote(
                payload.DailyRate,
                payload.Total,
                payload.Currency,
                payload.Breakdown.Select(line => new PricingLine(line.Concept, line.Amount)).ToList());
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            logger.LogError(exception, "Pricing service call failed.");
            throw new ExternalServiceUnavailableException("pricing", exception);
        }
    }

    private sealed record PricingQuoteRequest(
        string VehicleClass,
        decimal BaseDailyRate,
        int Days,
        IReadOnlyList<string> Extras,
        string Currency);

    private sealed record PricingQuoteResponse(
        decimal DailyRate,
        decimal Total,
        string Currency,
        IReadOnlyList<PricingBreakdownLine> Breakdown);

    private sealed record PricingBreakdownLine(string Concept, decimal Amount);
}

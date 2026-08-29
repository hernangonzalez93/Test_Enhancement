using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Pricing.Api;

namespace Pricing.Api.Tests;

/// <summary>
/// Contrato HTTP del servicio Pricing. Es el mismo contrato que consume el
/// adaptador PricingHttpCalculator de Rentals, y por eso importa fijarlo aqui.
/// </summary>
public sealed class PricingApiTests(WebApplicationFactory<PricingApiMarker> factory)
    : IClassFixture<WebApplicationFactory<PricingApiMarker>>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Health_reports_the_service_name()
    {
        var response = await _client.GetAsync("/health");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("pricing");
    }

    [Fact]
    public async Task A_valid_quote_returns_200_with_daily_rate_total_and_breakdown()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/quotes",
            new QuoteRequest("suv", 60m, 3, ["gps"], "USD"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var quote = await response.Content.ReadFromJsonAsync<QuoteResponse>();
        quote.ShouldNotBeNull();
        quote.DailyRate.ShouldBe(86m);
        quote.Total.ShouldBe(258m);
        quote.Currency.ShouldBe("USD");
        quote.Breakdown.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task An_unknown_vehicle_class_returns_400_with_problem_details()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/quotes",
            new QuoteRequest("submarine", 60m, 3, [], "USD"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("pricing.invalid_request");
    }

    [Fact]
    public async Task Zero_days_returns_400()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/quotes",
            new QuoteRequest("economy", 30m, 0, [], "USD"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_catalog_endpoint_exposes_classes_and_extras()
    {
        var response = await _client.GetAsync("/api/pricing/catalog");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("luxury");
        body.ShouldContain("child-seat");
    }
}

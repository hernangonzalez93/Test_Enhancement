using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Insurances.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Insurances.Api.Tests;

/// <summary>
/// La API con el consumidor de Kafka apagado por configuracion, igual que en
/// Notifications: se prueba el contrato HTTP sin necesidad de broker.
/// </summary>
public sealed class InsurancesApiFactory : WebApplicationFactory<InsurancesApiMarker>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Kafka:Enabled", "false");
    }
}

public sealed class InsuranceApiTests(InsurancesApiFactory factory) : IClassFixture<InsurancesApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    /// <summary>
    /// El convertidor de enums tambien hace falta al LEER: un cliente que no lo
    /// configure fallara al deserializar "Active". Configurarlo aqui reproduce
    /// lo que tiene que hacer cualquier consumidor de esta API.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task Health_reports_the_service_name()
    {
        var response = await _client.GetAsync("/health");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("insurances");
    }

    [Fact]
    public async Task Readiness_passes_when_the_consumer_is_disabled()
    {
        // Con el consumidor apagado no hay nada que esperar: el servicio esta listo.
        (await _client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_valid_quote_returns_200_with_premium_and_breakdown()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/insurance/quotes",
            new PremiumRequest("premium", 2, 1000m, "USD"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var quote = await response.Content.ReadFromJsonAsync<PremiumQuote>();
        quote.ShouldNotBeNull();
        quote.Coverage.ShouldBe("premium");
        quote.Premium.ShouldBe(180m);   // 18% de 1000 supera el minimo de 15x2
        quote.Breakdown.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task An_unknown_coverage_returns_400_with_problem_details()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/insurance/quotes",
            new PremiumRequest("golden", 2, 100m, "USD"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("insurance.invalid_request");
    }

    [Fact]
    public async Task The_coverage_catalog_is_exposed()
    {
        var body = await (await _client.GetAsync("/api/insurance/coverages")).Content.ReadAsStringAsync();

        body.ShouldContain("basic");
        body.ShouldContain("premium");
    }

    [Fact]
    public async Task Policies_stored_through_the_port_are_exposed_by_the_api()
    {
        var rentalId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var store = factory.Services.GetRequiredService<IPolicyStore>();
        await store.SaveAsync(new Policy(
            Policy.NumberFor(rentalId), rentalId, customerId, "standard", 27m, "USD",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(3), PolicyStatus.Active, DateTimeOffset.UtcNow));

        var policies = await _client.GetFromJsonAsync<List<Policy>>($"/api/policies?rentalId={rentalId}", JsonOptions);

        policies.ShouldNotBeNull().ShouldHaveSingleItem().Number.ShouldBe(Policy.NumberFor(rentalId));
    }

    [Fact]
    public async Task The_policy_status_travels_as_text_and_not_as_a_number()
    {
        // Sin JsonStringEnumConverter el enum saldria como 0/1/2 y el frontal
        // mostraria un numero al usuario.
        var rentalId = Guid.NewGuid();
        var store = factory.Services.GetRequiredService<IPolicyStore>();
        await store.SaveAsync(new Policy(
            Policy.NumberFor(rentalId), rentalId, Guid.NewGuid(), "standard", 27m, "USD",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(3), PolicyStatus.Active, DateTimeOffset.UtcNow));

        var body = await _client.GetStringAsync($"/api/policies?rentalId={rentalId}");

        body.ShouldContain("\"status\":\"Active\"");
    }

    [Fact]
    public async Task Filtering_by_an_unknown_rental_returns_an_empty_list()
    {
        var policies = await _client.GetFromJsonAsync<List<Policy>>($"/api/policies?rentalId={Guid.NewGuid()}", JsonOptions);

        policies.ShouldNotBeNull().ShouldBeEmpty();
    }
}

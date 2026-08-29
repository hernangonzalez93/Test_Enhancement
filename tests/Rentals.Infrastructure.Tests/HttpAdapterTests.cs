using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Rentals.Application.Abstractions;
using Rentals.Domain.Model;
using Rentals.Infrastructure.Http;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Rentals.Infrastructure.Tests;

/// <summary>
/// Adaptadores HTTP contra un servidor simulado (WireMock). No se levanta el
/// servicio real: lo que se prueba es la TRADUCCION del protocolo, incluidos
/// los caminos que el servicio real casi nunca produce (503, timeouts, JSON
/// inesperado) y que en produccion son justamente los que rompen.
/// </summary>
public sealed class HttpAdapterTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Dispose();

    private HttpClient ClientFor() => new() { BaseAddress = new Uri(_server.Url!), Timeout = TimeSpan.FromSeconds(5) };

    // --- Fleet ---------------------------------------------------------

    [Fact]
    public async Task Fleet_adapter_maps_a_successful_response_into_a_vehicle_snapshot()
    {
        var vehicleId = VehicleId.New();
        _server
            .Given(Request.Create().WithPath($"/api/vehicles/{vehicleId.Value}").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.OK)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""
                    {
                      "id": "{{vehicleId.Value}}",
                      "model": "Toyota RAV4",
                      "vehicleClass": "suv",
                      "dailyRate": 60.00,
                      "currency": "USD",
                      "available": true
                    }
                    """));

        var adapter = new FleetHttpVehicleCatalog(ClientFor(), NullLogger<FleetHttpVehicleCatalog>.Instance);

        var snapshot = await adapter.FindAsync(vehicleId);

        snapshot.ShouldNotBeNull();
        snapshot.Model.ShouldBe("Toyota RAV4");
        snapshot.VehicleClass.ShouldBe("suv");
        snapshot.BaseDailyRate.ShouldBe(60.00m);
        snapshot.Available.ShouldBeTrue();
    }

    [Fact]
    public async Task Fleet_adapter_turns_a_404_into_null_because_that_is_not_a_failure()
    {
        _server
            .Given(Request.Create().WithPath("/api/vehicles/*").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.NotFound));

        var adapter = new FleetHttpVehicleCatalog(ClientFor(), NullLogger<FleetHttpVehicleCatalog>.Instance);

        (await adapter.FindAsync(VehicleId.New())).ShouldBeNull();
    }

    [Fact]
    public async Task Fleet_adapter_turns_a_500_into_a_service_unavailable_exception()
    {
        _server
            .Given(Request.Create().WithPath("/api/vehicles/*").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.InternalServerError));

        var adapter = new FleetHttpVehicleCatalog(ClientFor(), NullLogger<FleetHttpVehicleCatalog>.Instance);

        var exception = await Should.ThrowAsync<ExternalServiceUnavailableException>(
            () => adapter.FindAsync(VehicleId.New()));

        exception.ServiceName.ShouldBe("fleet");
    }

    [Fact]
    public async Task Fleet_adapter_turns_a_timeout_into_a_service_unavailable_exception()
    {
        _server
            .Given(Request.Create().WithPath("/api/vehicles/*").UsingGet())
            .RespondWith(Response.Create().WithDelay(TimeSpan.FromSeconds(3)).WithStatusCode(HttpStatusCode.OK));

        using var client = new HttpClient
        {
            BaseAddress = new Uri(_server.Url!),
            Timeout = TimeSpan.FromMilliseconds(300)
        };
        var adapter = new FleetHttpVehicleCatalog(client, NullLogger<FleetHttpVehicleCatalog>.Instance);

        await Should.ThrowAsync<ExternalServiceUnavailableException>(() => adapter.FindAsync(VehicleId.New()));
    }

    // --- Pricing -------------------------------------------------------

    [Fact]
    public async Task Pricing_adapter_maps_the_quote_and_its_breakdown()
    {
        _server
            .Given(Request.Create().WithPath("/api/quotes").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.OK)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                    {
                      "dailyRate": 81.00,
                      "total": 243.00,
                      "currency": "USD",
                      "breakdown": [
                        { "concept": "base", "amount": 60.00 },
                        { "concept": "class:suv", "amount": 21.00 }
                      ]
                    }
                    """));

        var adapter = new PricingHttpCalculator(ClientFor(), NullLogger<PricingHttpCalculator>.Instance);

        var quote = await adapter.QuoteAsync(new PricingRequest("suv", 60m, 3, [], "USD"));

        quote.DailyRate.ShouldBe(81.00m);
        quote.Total.ShouldBe(243.00m);
        quote.Breakdown.Count.ShouldBe(2);
        quote.Breakdown[1].Concept.ShouldBe("class:suv");
    }

    [Fact]
    public async Task Pricing_adapter_sends_the_request_body_the_service_expects()
    {
        _server
            .Given(Request.Create().WithPath("/api/quotes").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.OK)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"dailyRate":30,"total":90,"currency":"USD","breakdown":[]}"""));

        var adapter = new PricingHttpCalculator(ClientFor(), NullLogger<PricingHttpCalculator>.Instance);
        await adapter.QuoteAsync(new PricingRequest("economy", 30m, 3, ["gps"], "USD"));

        var request = _server.LogEntries.Single().RequestMessage.ShouldNotBeNull();
        var body = request.Body.ShouldNotBeNull();
        body.ShouldContain("\"vehicleClass\":\"economy\"");
        body.ShouldContain("\"days\":3");
        body.ShouldContain("\"gps\"");
    }

    [Fact]
    public async Task Pricing_adapter_turns_a_400_into_a_service_unavailable_exception()
    {
        _server
            .Given(Request.Create().WithPath("/api/quotes").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.BadRequest));

        var adapter = new PricingHttpCalculator(ClientFor(), NullLogger<PricingHttpCalculator>.Instance);

        var exception = await Should.ThrowAsync<ExternalServiceUnavailableException>(
            () => adapter.QuoteAsync(new PricingRequest("economy", 30m, 3, [], "USD")));

        exception.ServiceName.ShouldBe("pricing");
    }

    [Fact]
    public async Task Pricing_adapter_fails_when_the_service_is_not_listening()
    {
        using var client = new HttpClient
        {
            // Puerto cerrado a proposito: simula el servicio caido.
            BaseAddress = new Uri("http://127.0.0.1:1"),
            Timeout = TimeSpan.FromSeconds(2)
        };
        var adapter = new PricingHttpCalculator(client, NullLogger<PricingHttpCalculator>.Instance);

        await Should.ThrowAsync<ExternalServiceUnavailableException>(
            () => adapter.QuoteAsync(new PricingRequest("economy", 30m, 3, [], "USD")));
    }
}

using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rentals.Application.Abstractions;
using Rentals.Infrastructure;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Rentals.Infrastructure.Tests;

/// <summary>
/// Verifica el cableado real de la inyeccion de dependencias y la politica de
/// reintentos. Es una prueba de adaptador, no de unidad: se resuelve el puerto
/// desde el contenedor, igual que hace la API en produccion.
/// </summary>
public sealed class ResilienceAndWiringTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Dispose();

    private ServiceProvider BuildProvider() => new ServiceCollection()
        .AddLogging()
        .AddRentalsInfrastructure(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:RentalsDatabase"] = "Host=localhost;Database=unused;Username=u;Password=p",
                ["Services:PricingBaseUrl"] = _server.Url!,
                ["Services:FleetBaseUrl"] = _server.Url!,
                // Timeout holgado a proposito. Lo que estas pruebas miden es el
                // NUMERO de intentos, no el tiempo: con 5 s, el HttpClient.Timeout
                // envuelve a los tres intentos y bajo carga los corta antes de
                // completarlos, haciendo fallar la asercion por un motivo ajeno.
                ["Services:TimeoutSeconds"] = "30",
                ["Services:MaxRetryAttempts"] = "2",
                ["Services:RetryDelayMilliseconds"] = "10"
            })
            .Build())
        .BuildServiceProvider();

    [Fact]
    public async Task A_transient_503_is_retried_and_the_second_attempt_succeeds()
    {
        const string scenario = "pricing-retry";

        _server
            .Given(Request.Create().WithPath("/api/quotes").UsingPost())
            .InScenario(scenario)
            .WillSetStateTo("recovered")
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.ServiceUnavailable));

        _server
            .Given(Request.Create().WithPath("/api/quotes").UsingPost())
            .InScenario(scenario)
            .WhenStateIs("recovered")
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.OK)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"dailyRate":30,"total":90,"currency":"USD","breakdown":[]}"""));

        await using var provider = BuildProvider();
        var pricing = provider.GetRequiredService<IPricingCalculator>();

        var quote = await pricing.QuoteAsync(new PricingRequest("economy", 30m, 3, [], "USD"));

        quote.Total.ShouldBe(90m);
        _server.LogEntries.Count().ShouldBe(2);
    }

    [Fact]
    public async Task After_exhausting_the_retries_the_call_reports_the_service_as_unavailable()
    {
        _server
            .Given(Request.Create().WithPath("/api/quotes").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.ServiceUnavailable));

        await using var provider = BuildProvider();
        var pricing = provider.GetRequiredService<IPricingCalculator>();

        await Should.ThrowAsync<ExternalServiceUnavailableException>(
            () => pricing.QuoteAsync(new PricingRequest("economy", 30m, 3, [], "USD")));

        // Un intento original + dos reintentos configurados.
        _server.LogEntries.Count().ShouldBe(3);
    }

    [Fact]
    public void The_container_resolves_every_output_port_of_the_application()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IRentalRepository>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IVehicleCatalog>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IPricingCalculator>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IClock>().ShouldNotBeNull();
    }
}

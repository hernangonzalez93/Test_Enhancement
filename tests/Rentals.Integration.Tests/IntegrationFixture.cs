using Fleet.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rentals.Application.Abstractions;
using Rentals.Infrastructure.Http;
using Rentals.Infrastructure.Persistence;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;
using WireMock.Server;

namespace Rentals.Integration.Tests;

/// <summary>
/// El escenario mas completo del repositorio: tres servicios reales hablando
/// entre si sobre PostgreSQL y Kafka reales.
///
///   Rentals.Api  --HTTP-->  Fleet.Api        (via el TestServer de Fleet)
///   Rentals.Api  --HTTP-->  WireMock         (Pricing simulado)
///   Rentals.Api  --Kafka--> Fleet + Notifications
///
/// Pricing se simula porque su unica logica ya esta cubierta por pruebas
/// unitarias; Fleet y Notifications son reales porque lo que interesa medir
/// aqui es precisamente la integracion entre servicios.
/// </summary>
public sealed class IntegrationFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("testenforce")
        .WithUsername("testenforce")
        .WithPassword("testenforce")
        .Build();

    private readonly KafkaContainer _kafka = new KafkaBuilder("confluentinc/cp-kafka:7.7.1").Build();

    private readonly string _consumerGroupSuffix = Guid.NewGuid().ToString("N")[..8];

    public WireMockServer Pricing { get; private set; } = default!;

    public WebApplicationFactory<FleetApiMarker> FleetApp { get; private set; } = default!;

    public WebApplicationFactory<Notifications.Api.NotificationsApiMarker> NotificationsApp { get; private set; } = default!;

    public WebApplicationFactory<Rentals.Api.RentalsApiMarker> RentalsApp { get; private set; } = default!;

    public string BootstrapServers => _kafka.GetBootstrapAddress();

    public string Topic { get; } = "rental-events-it-" + Guid.NewGuid().ToString("N")[..8];

    public HttpClient RentalsClient => RentalsApp.CreateClient();

    public HttpClient NotificationsClient => NotificationsApp.CreateClient();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _kafka.StartAsync());

        Pricing = WireMockServer.Start();
        StubPricing();

        FleetApp = new FleetTestApp(_postgres.GetConnectionString(), BootstrapServers, Topic, _consumerGroupSuffix);
        await MigrateFleetAsync();

        NotificationsApp = new NotificationsTestApp(BootstrapServers, Topic, _consumerGroupSuffix);
        // Fuerza el arranque del host para que el consumidor se suscriba.
        _ = NotificationsApp.CreateClient();

        RentalsApp = new RentalsTestApp(
            _postgres.GetConnectionString(),
            BootstrapServers,
            Topic,
            Pricing.Url!,
            FleetApp.Server.CreateHandler());

        await MigrateRentalsAsync();
    }

    private async Task MigrateFleetAsync()
    {
        using var scope = FleetApp.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
        await context.Database.MigrateAsync();
        await FleetSeed.EnsureSeededAsync(context);

        // El consumidor de Fleet arranca con el host: se fuerza aqui.
        _ = FleetApp.CreateClient();
    }

    private async Task MigrateRentalsAsync()
    {
        using var scope = RentalsApp.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RentalsDbContext>();
        await context.Database.MigrateAsync();
    }

    public RentalsDbContext CreateRentalsContext() =>
        new(new DbContextOptionsBuilder<RentalsDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options);

    public FleetDbContext CreateFleetContext() =>
        new(new DbContextOptionsBuilder<FleetDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options);

    public async Task ResetAsync()
    {
        await using var rentals = CreateRentalsContext();
        await rentals.Database.ExecuteSqlRawAsync("TRUNCATE TABLE rentals.rentals;");

        await using var fleet = CreateFleetContext();
        await fleet.Database.ExecuteSqlRawAsync("TRUNCATE TABLE fleet.vehicles;");
        await FleetSeed.EnsureSeededAsync(fleet);

        PricingDailyRate = 50m;
        PricingIsDown = false;
    }

    /// <summary>Tarifa diaria que devuelve el Pricing simulado.</summary>
    public decimal PricingDailyRate { get; set; } = 50m;

    /// <summary>Cuando es true, el Pricing simulado responde 500.</summary>
    public bool PricingIsDown { get; set; }

    /// <summary>
    /// Se registra UNA sola vez y responde segun el estado mutable de arriba.
    /// Reconfigurar WireMock entre pruebas cerraba las conexiones que el
    /// HttpClient del servicio mantenia en el pool, y eso producia fallos
    /// intermitentes dificiles de leer.
    /// </summary>
    private void StubPricing()
    {
        Pricing
            .Given(WireMock.RequestBuilders.Request.Create().WithPath("/api/quotes").UsingPost())
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithCallback(_ =>
            {
                if (PricingIsDown)
                {
                    return new WireMock.ResponseMessage { StatusCode = 500 };
                }

                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    dailyRate = PricingDailyRate,
                    total = PricingDailyRate * 3,
                    currency = "USD",
                    breakdown = Array.Empty<object>()
                });

                return new WireMock.ResponseMessage
                {
                    StatusCode = 200,
                    Headers = new Dictionary<string, WireMock.Types.WireMockList<string>>
                    {
                        ["Content-Type"] = new("application/json")
                    },
                    BodyData = new WireMock.Util.BodyData
                    {
                        DetectedBodyType = WireMock.Types.BodyType.String,
                        BodyAsString = json
                    }
                };
            }));
    }

    /// <summary>
    /// Espera activa acotada. En un sistema por eventos la propagacion es
    /// asincrona: afirmar de inmediato produce pruebas intermitentes.
    /// </summary>
    public static async Task<bool> EventuallyAsync(
        Func<Task<bool>> condition,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(30));
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(250);

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(interval);
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        Pricing?.Dispose();
        if (RentalsApp is not null)
        {
            await RentalsApp.DisposeAsync();
        }

        if (NotificationsApp is not null)
        {
            await NotificationsApp.DisposeAsync();
        }

        if (FleetApp is not null)
        {
            await FleetApp.DisposeAsync();
        }

        await _kafka.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private sealed class FleetTestApp(string connectionString, string bootstrap, string topic, string groupSuffix)
        : WebApplicationFactory<FleetApiMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:FleetDatabase", connectionString);
            builder.UseSetting("Database:AutoMigrate", "false");
            builder.UseSetting("Kafka:Enabled", "true");
            builder.UseSetting("Kafka:BootstrapServers", bootstrap);
            builder.UseSetting("Kafka:Topic", topic);
            builder.UseSetting("Kafka:GroupId", "fleet-it-" + groupSuffix);
        }
    }

    private sealed class NotificationsTestApp(string bootstrap, string topic, string groupSuffix)
        : WebApplicationFactory<Notifications.Api.NotificationsApiMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Kafka:Enabled", "true");
            builder.UseSetting("Kafka:BootstrapServers", bootstrap);
            builder.UseSetting("Kafka:Topic", topic);
            builder.UseSetting("Kafka:GroupId", "notifications-it-" + groupSuffix);
        }
    }

    private sealed class RentalsTestApp(
        string connectionString,
        string bootstrap,
        string topic,
        string pricingUrl,
        HttpMessageHandler fleetHandler) : WebApplicationFactory<Rentals.Api.RentalsApiMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:RentalsDatabase", connectionString);
            builder.UseSetting("Database:AutoMigrate", "false");
            builder.UseSetting("Kafka:BootstrapServers", bootstrap);
            builder.UseSetting("Kafka:RentalEventsTopic", topic);
            builder.UseSetting("Services:PricingBaseUrl", pricingUrl);
            builder.UseSetting("Services:FleetBaseUrl", "http://fleet.local");

            builder.ConfigureServices(services =>
            {
                // El adaptador HTTP de Fleet se conecta al TestServer del
                // servicio Fleet real: hay serializacion, enrutado y base de
                // datos de verdad, pero sin abrir un puerto TCP.
                services
                    .AddHttpClient<IVehicleCatalog, FleetHttpVehicleCatalog>(client =>
                        client.BaseAddress = new Uri("http://fleet.local"))
                    .ConfigurePrimaryHttpMessageHandler(() => fleetHandler);
            });
        }
    }
}

[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<IntegrationFixture>
{
    public const string Name = "integration";
}

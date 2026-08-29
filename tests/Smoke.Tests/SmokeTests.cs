using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Smoke.Tests;

/// <summary>
/// Pruebas de humo: se ejecutan CONTRA UN DESPLIEGUE YA LEVANTADO
/// (docker compose up), no contra codigo en memoria. Responden a una unica
/// pregunta: "el despliegue esta vivo y correctamente cableado?".
///
/// Por eso son pocas, rapidas, sin datos de prueba complicados y sin afirmar
/// reglas de negocio: eso ya lo cubren las capas de abajo.
///
///   docker compose up -d --wait
///   dotnet test tests/Smoke.Tests
///
/// Las URLs se pueden sobrescribir por variables de entorno para apuntar a un
/// entorno remoto (por ejemplo en un pipeline de despliegue).
/// </summary>
public sealed class SmokeTests : IDisposable
{
    private static string Url(string variable, string fallback) =>
        Environment.GetEnvironmentVariable(variable) ?? fallback;

    private static readonly string RentalsUrl = Url("SMOKE_RENTALS_URL", "http://localhost:5101");
    private static readonly string PricingUrl = Url("SMOKE_PRICING_URL", "http://localhost:5102");
    private static readonly string FleetUrl = Url("SMOKE_FLEET_URL", "http://localhost:5103");
    private static readonly string NotificationsUrl = Url("SMOKE_NOTIFICATIONS_URL", "http://localhost:5104");
    private static readonly string InsurancesUrl = Url("SMOKE_INSURANCES_URL", "http://localhost:5106");
    private static readonly string BillingUrl = Url("SMOKE_BILLING_URL", "http://localhost:5107");
    private static readonly string FrontendUrl = Url("SMOKE_FRONTEND_URL", "http://localhost:5173");

    private static string BaseUrlFor(string service) => service switch
    {
        "rentals" => RentalsUrl,
        "pricing" => PricingUrl,
        "fleet" => FleetUrl,
        "insurances" => InsurancesUrl,
        "billing" => BillingUrl,
        _ => NotificationsUrl
    };

    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(15) };

    public void Dispose() => _client.Dispose();

    [Theory]
    [InlineData("rentals")]
    [InlineData("pricing")]
    [InlineData("fleet")]
    [InlineData("notifications")]
    [InlineData("insurances")]
    [InlineData("billing")]
    public async Task Every_service_answers_its_liveness_probe(string service)
    {
        var response = await _client.GetAsync($"{BaseUrlFor(service)}/health");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("rentals")]
    [InlineData("fleet")]
    [InlineData("notifications")]
    [InlineData("insurances")]
    [InlineData("billing")]
    public async Task Every_service_answers_its_readiness_probe(string service)
    {
        var baseUrl = BaseUrlFor(service);

        // /health/ready es mas exigente que /health: solo responde 200 si el
        // servicio puede TRABAJAR. En Rentals y Fleet incluye que PostgreSQL
        // sea accesible; en Fleet y Notifications, ademas, que su consumidor de
        // Kafka ya tenga particiones asignadas.
        (await _client.GetAsync($"{baseUrl}/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_fleet_is_seeded_with_vehicles()
    {
        var vehicles = await _client.GetFromJsonAsync<JsonElement>($"{FleetUrl}/api/vehicles");

        vehicles.GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task The_rentals_database_has_its_migrations_applied()
    {
        // Si la migracion no se hubiera aplicado, esta consulta fallaria con 500.
        var response = await _client.GetAsync($"{RentalsUrl}/api/rentals?customerId={Guid.NewGuid()}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Pricing_answers_a_real_quote()
    {
        var response = await _client.PostAsJsonAsync(
            $"{PricingUrl}/api/quotes",
            new { vehicleClass = "economy", baseDailyRate = 30m, days = 3, extras = Array.Empty<string>(), currency = "USD" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var quote = await response.Content.ReadFromJsonAsync<JsonElement>();
        quote.GetProperty("total").GetDecimal().ShouldBe(90m);
    }

    [Fact]
    public async Task The_frontend_is_served()
    {
        var response = await _client.GetAsync(FrontendUrl);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("<div id=\"root\">");
    }

    [Fact]
    public async Task The_frontend_proxies_every_backend_service()
    {
        // Una sola prueba cubre la configuracion de nginx, que es justo lo que
        // usan las pruebas E2E: si el proxy esta mal, fallan todas a la vez.
        (await _client.GetAsync($"{FrontendUrl}/api/vehicles")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.GetAsync($"{FrontendUrl}/api/notifications")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.GetAsync($"{FrontendUrl}/api/rentals?customerId={Guid.NewGuid()}"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.GetAsync($"{FrontendUrl}/api/policies?rentalId={Guid.NewGuid()}"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.GetAsync($"{FrontendUrl}/api/invoices?rentalId={Guid.NewGuid()}"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.GetAsync($"{FrontendUrl}/api/insurance/coverages"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Insurances_answers_a_real_premium_quote()
    {
        var response = await _client.PostAsJsonAsync(
            $"{InsurancesUrl}/api/insurance/quotes",
            new { coverage = "standard", days = 3, rentalTotal = 150m, currency = "USD" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var quote = await response.Content.ReadFromJsonAsync<JsonElement>();
        // minimo 9/dia x 3 = 27, frente al 12 % de 150 = 18.
        quote.GetProperty("premium").GetDecimal().ShouldBe(27m);
    }

    [Fact]
    public async Task The_billing_database_has_its_migrations_applied()
    {
        var response = await _client.GetAsync($"{BillingUrl}/api/invoices?customerId={Guid.NewGuid()}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_end_to_end_rental_can_be_created_confirmed_and_notified()
    {
        var customerId = Guid.NewGuid();
        var vehicles = await _client.GetFromJsonAsync<JsonElement>($"{FleetUrl}/api/vehicles?availableOnly=true");
        vehicles.GetArrayLength().ShouldBeGreaterThan(0);
        var vehicleId = vehicles[0].GetProperty("id").GetGuid();

        var start = DateTimeOffset.UtcNow.AddDays(30);
        var create = await _client.PostAsJsonAsync($"{RentalsUrl}/api/rentals", new
        {
            customerId,
            vehicleId,
            periodStart = start,
            periodEnd = start.AddDays(2),
            licenseNumber = "LIC-99999",
            licenseExpiresOn = start.AddYears(2),
            extras = Array.Empty<string>()
        });

        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        var rental = await create.Content.ReadFromJsonAsync<JsonElement>();
        var rentalId = rental.GetProperty("id").GetGuid();

        (await _client.PostAsync($"{RentalsUrl}/api/rentals/{rentalId}/confirm", null))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // La notificacion viaja por Kafka: se espera, no se asume.
        var notified = await EventuallyAsync(async () =>
        {
            var notifications = await _client.GetFromJsonAsync<JsonElement>(
                $"{NotificationsUrl}/api/notifications?customerId={customerId}");

            return notifications.EnumerateArray()
                .Any(n => n.GetProperty("eventType").GetString() == "rental.confirmed");
        });

        notified.ShouldBeTrue("la confirmacion debia llegar a Notifications a traves de Kafka.");

        // Insurances consume los mismos eventos y activa la poliza de la renta.
        var insured = await EventuallyAsync(async () =>
        {
            var policies = await _client.GetFromJsonAsync<JsonElement>(
                $"{InsurancesUrl}/api/policies?rentalId={rentalId}");

            return policies.GetArrayLength() > 0
                   && policies[0].GetProperty("status").GetString() == "Active";
        });

        insured.ShouldBeTrue("Insurances debia emitir y activar la poliza de la renta.");
    }

    private static async Task<bool> EventuallyAsync(Func<Task<bool>> condition, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(500);
        }

        return false;
    }
}

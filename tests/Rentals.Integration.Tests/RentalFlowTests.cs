using System.Net;
using System.Net.Http.Json;
using System.Text;
using Confluent.Kafka;
using Fleet.Api;
using Microsoft.EntityFrameworkCore;
using Notifications.Api;
using Rentals.Api.Endpoints;
using Rentals.Application.Rentals;
using Rentals.Domain.Model;
using Shared.Contracts;

namespace Rentals.Integration.Tests;

/// <summary>
/// Nivel 4: integracion real entre servicios. Nada esta sustituido salvo
/// Pricing. Estas pruebas son las unicas capaces de detectar fallos de
/// cableado: una cadena de conexion mal formada, un topico distinto entre
/// productor y consumidor, o un mapeo de EF que solo falla contra PostgreSQL.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class RentalFlowTests(IntegrationFixture fixture) : IAsyncLifetime
{
    private readonly HttpClient _client = fixture.RentalsClient;

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static readonly DateTimeOffset Start = DateTimeOffset.UtcNow.AddDays(10);

    private static CreateRentalRequest Request(Guid? customerId = null, Guid? vehicleId = null, int days = 3) => new(
        customerId ?? Guid.NewGuid(),
        vehicleId ?? FleetSeed.EconomyVehicleId,
        Start,
        Start.AddDays(days),
        "LIC-12345",
        Start.AddYears(2),
        []);

    private async Task<RentalDto> CreateRentalAsync(CreateRentalRequest? request = null)
    {
        var response = await _client.PostAsJsonAsync("/api/rentals", request ?? Request());
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await response.Content.ReadFromJsonAsync<RentalDto>()).ShouldNotBeNull();
    }

    [Fact]
    public async Task Requesting_a_rental_writes_a_row_in_postgresql()
    {
        var dto = await CreateRentalAsync();

        await using var context = fixture.CreateRentalsContext();
        var stored = await context.Rentals.SingleAsync(r => r.Id == RentalId.From(dto.Id));

        stored.Status.ShouldBe(RentalStatus.Pending);
        stored.EstimatedTotal.Amount.ShouldBe(150m);
        stored.Period.TotalDays.ShouldBe(3);
    }

    [Fact]
    public async Task The_rate_comes_from_the_pricing_service_over_http()
    {
        fixture.PricingDailyRate = 77m;

        var dto = await CreateRentalAsync();

        dto.DailyRate.ShouldBe(77m);
        dto.EstimatedTotal.ShouldBe(231m);
    }

    [Fact]
    public async Task The_vehicle_data_comes_from_the_real_fleet_service()
    {
        // Fleet solo conoce estos ids; si el adaptador HTTP no llegara al
        // servicio real, la peticion terminaria en 404.
        var response = await _client.PostAsJsonAsync("/api/rentals", Request(vehicleId: FleetSeed.LuxuryVehicleId));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task An_unknown_vehicle_is_reported_as_404_end_to_end()
    {
        var response = await _client.PostAsJsonAsync("/api/rentals", Request(vehicleId: Guid.NewGuid()));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).ShouldContain("vehicle.not_found");
    }

    [Fact]
    public async Task A_second_rental_overlapping_the_same_vehicle_is_rejected_with_409()
    {
        var vehicleId = FleetSeed.SuvVehicleId;
        await CreateRentalAsync(Request(vehicleId: vehicleId, days: 5));

        var response = await _client.PostAsJsonAsync("/api/rentals", Request(vehicleId: vehicleId, days: 2));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldContain("rental.overlapping");
    }

    [Fact]
    public async Task When_pricing_is_down_the_api_answers_503()
    {
        fixture.PricingIsDown = true;

        var response = await _client.PostAsJsonAsync("/api/rentals", Request());

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).ShouldContain("pricing.unavailable");
    }

    [Fact]
    public async Task Requesting_a_rental_publishes_the_event_on_kafka()
    {
        var dto = await CreateRentalAsync();

        var messages = ConsumeEventsFor(dto.Id, expected: 1);

        EventTypeOf(messages.ShouldHaveSingleItem()).ShouldBe(IntegrationEventTypes.RentalRequested);
    }

    [Fact]
    public async Task Each_transition_publishes_its_event_while_the_business_rules_stay_alive()
    {
        var dto = await CreateRentalAsync(Request(vehicleId: FleetSeed.CompactVehicleId));

        (await _client.PostAsync($"/api/rentals/{dto.Id}/confirm", null)).EnsureSuccessStatusCode();

        // La renta empieza en el futuro, asi que retirar el vehiculo hoy se
        // rechaza: la regla de negocio sigue viva a traves de toda la pila.
        var tooEarly = await _client.PostAsync($"/api/rentals/{dto.Id}/start", null);
        tooEarly.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var types = ConsumeEventsFor(dto.Id, expected: 2).Select(EventTypeOf).ToArray();

        types.ShouldBe([IntegrationEventTypes.RentalRequested, IntegrationEventTypes.RentalConfirmed]);
    }

    [Fact]
    public async Task Confirming_a_rental_makes_fleet_mark_the_vehicle_as_unavailable()
    {
        var dto = await CreateRentalAsync(Request(vehicleId: FleetSeed.LuxuryVehicleId));
        (await _client.PostAsync($"/api/rentals/{dto.Id}/confirm", null)).EnsureSuccessStatusCode();

        var updated = await IntegrationFixture.EventuallyAsync(async () =>
        {
            await using var context = fixture.CreateFleetContext();
            var vehicle = await context.Vehicles.AsNoTracking()
                .SingleAsync(v => v.Id == FleetSeed.LuxuryVehicleId);
            return !vehicle.Available;
        });

        updated.ShouldBeTrue("Fleet debia consumir rental.confirmed y bloquear el vehiculo.");
    }

    [Fact]
    public async Task Confirming_a_rental_produces_a_notification_in_the_notifications_service()
    {
        var customerId = Guid.NewGuid();
        var dto = await CreateRentalAsync(Request(customerId: customerId, vehicleId: FleetSeed.EconomyVehicleId));
        (await _client.PostAsync($"/api/rentals/{dto.Id}/confirm", null)).EnsureSuccessStatusCode();

        using var notificationsClient = fixture.NotificationsClient;

        var arrived = await IntegrationFixture.EventuallyAsync(async () =>
        {
            var notifications = await notificationsClient
                .GetFromJsonAsync<List<Notification>>($"/api/notifications?customerId={customerId}");

            return notifications is not null
                   && notifications.Any(n => n.EventType == IntegrationEventTypes.RentalConfirmed);
        });

        arrived.ShouldBeTrue("Notifications debia consumir rental.confirmed del mismo topico.");
    }

    [Fact]
    public async Task Cancelling_a_rental_persists_the_refund_and_publishes_it()
    {
        var dto = await CreateRentalAsync(Request(vehicleId: FleetSeed.CompactVehicleId));
        (await _client.PostAsync($"/api/rentals/{dto.Id}/confirm", null)).EnsureSuccessStatusCode();

        var response = await _client.PostAsync($"/api/rentals/{dto.Id}/cancel", null);
        var cancelled = (await response.Content.ReadFromJsonAsync<RentalDto>()).ShouldNotBeNull();

        cancelled.Status.ShouldBe(nameof(RentalStatus.Cancelled));
        cancelled.RefundAmount.ShouldBe(150m);

        await using var context = fixture.CreateRentalsContext();
        var stored = await context.Rentals.SingleAsync(r => r.Id == RentalId.From(dto.Id));
        stored.RefundAmount.ShouldNotBeNull().Amount.ShouldBe(150m);
    }

    [Fact]
    public async Task Listing_by_customer_returns_what_was_persisted()
    {
        var customerId = Guid.NewGuid();
        await CreateRentalAsync(Request(customerId: customerId, vehicleId: FleetSeed.EconomyVehicleId));
        await CreateRentalAsync(Request(customerId: customerId, vehicleId: FleetSeed.SuvVehicleId));
        await CreateRentalAsync(Request(vehicleId: FleetSeed.LuxuryVehicleId));

        var rentals = await _client.GetFromJsonAsync<List<RentalDto>>($"/api/rentals?customerId={customerId}");

        rentals.ShouldNotBeNull().Count.ShouldBe(2);
        rentals.ShouldAllBe(r => r.CustomerId == customerId);
    }

    private static string EventTypeOf(ConsumeResult<string, string> message)
    {
        message.Message.Headers.TryGetLastBytes(EventHeaders.EventType, out var bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Lector independiente del topico. Filtra por clave de particion porque el
    /// topico es compartido por toda la clase: solo interesan los eventos de la
    /// renta bajo prueba, y su orden relativo esta garantizado por la clave.
    /// </summary>
    private List<ConsumeResult<string, string>> ConsumeEventsFor(
        Guid rentalId,
        int expected,
        TimeSpan? timeout = null)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = fixture.BootstrapServers,
            GroupId = "assert-" + Guid.NewGuid().ToString("N")[..8],
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            AllowAutoCreateTopics = true
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(fixture.Topic);

        var key = rentalId.ToString();
        var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(30));
        var results = new List<ConsumeResult<string, string>>();

        while (results.Count < expected && DateTime.UtcNow < deadline)
        {
            var result = consumer.Consume(TimeSpan.FromMilliseconds(500));
            if (result?.Message is not null && result.Message.Key == key)
            {
                results.Add(result);
            }
        }

        consumer.Close();
        return results;
    }
}

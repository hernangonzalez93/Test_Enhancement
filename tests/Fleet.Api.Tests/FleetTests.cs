using System.Net;
using System.Net.Http.Json;
using Fleet.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Contracts;

namespace Fleet.Api.Tests;

[Collection(FleetCollection.Name)]
public sealed class FleetApiTests(FleetFixture fixture) : IAsyncLifetime
{
    private readonly HttpClient _client = fixture.CreateClient();

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Health_reports_the_service_name()
    {
        (await (await _client.GetAsync("/health")).Content.ReadAsStringAsync()).ShouldContain("fleet");
    }

    [Fact]
    public async Task The_readiness_probe_requires_a_reachable_database()
    {
        (await _client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Listing_vehicles_returns_the_whole_fleet_ordered_by_model()
    {
        var vehicles = await _client.GetFromJsonAsync<List<VehicleResponse>>("/api/vehicles");

        vehicles.ShouldNotBeNull().Count.ShouldBe(FleetSeed.Vehicles.Count);
        vehicles.Select(v => v.Model).ShouldBe(vehicles.Select(v => v.Model).OrderBy(m => m));
    }

    [Fact]
    public async Task Listing_with_availableOnly_hides_rented_vehicles()
    {
        await using (var context = fixture.CreateContext())
        {
            var vehicle = await context.Vehicles.FirstAsync(v => v.Id == FleetSeed.SuvVehicleId);
            vehicle.Available = false;
            await context.SaveChangesAsync();
        }

        var vehicles = await _client.GetFromJsonAsync<List<VehicleResponse>>("/api/vehicles?availableOnly=true");

        vehicles.ShouldNotBeNull().Count.ShouldBe(FleetSeed.Vehicles.Count - 1);
        vehicles.ShouldNotContain(v => v.Id == FleetSeed.SuvVehicleId);
    }

    [Fact]
    public async Task Fetching_a_vehicle_returns_the_shape_the_rentals_adapter_expects()
    {
        var vehicle = await _client.GetFromJsonAsync<VehicleResponse>($"/api/vehicles/{FleetSeed.SuvVehicleId}");

        vehicle.ShouldNotBeNull();
        vehicle.Id.ShouldBe(FleetSeed.SuvVehicleId);
        vehicle.VehicleClass.ShouldBe("suv");
        vehicle.DailyRate.ShouldBe(60m);
        vehicle.Currency.ShouldBe("USD");
        vehicle.Available.ShouldBeTrue();
    }

    [Fact]
    public async Task Fetching_an_unknown_vehicle_returns_404_with_an_error_code()
    {
        var response = await _client.GetAsync($"/api/vehicles/{Guid.NewGuid()}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).ShouldContain("vehicle.not_found");
    }

    [Fact]
    public async Task Creating_a_vehicle_returns_201_and_stores_it_available()
    {
        var request = new CreateVehicleRequest(null, "Kia Picanto", "economy", "new-100", 28m, "usd");

        var response = await _client.PostAsJsonAsync("/api/vehicles", request);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<VehicleResponse>();
        created.ShouldNotBeNull();
        created.LicensePlate.ShouldBe("NEW-100");
        created.Currency.ShouldBe("USD");
        created.Available.ShouldBeTrue();

        await using var context = fixture.CreateContext();
        (await context.Vehicles.AnyAsync(v => v.Id == created.Id)).ShouldBeTrue();
    }

    [Fact]
    public async Task Creating_a_vehicle_without_a_model_is_rejected_with_400()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/vehicles",
            new CreateVehicleRequest(null, "", "economy", "ABC-123", 28m, "USD"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}

/// <summary>
/// La regla de disponibilidad, aislada del transporte. El consumidor de Kafka
/// solo traduce bytes; todo lo que decide algo esta aqui y se prueba contra la
/// base de datos real, sin broker.
/// </summary>
[Collection(FleetCollection.Name)]
public sealed class VehicleAvailabilityHandlerTests(FleetFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<bool> HandleAsync(IIntegrationEvent integrationEvent)
    {
        await using var context = fixture.CreateContext();
        var handler = new VehicleAvailabilityHandler(context, NullLogger<VehicleAvailabilityHandler>.Instance);
        return await handler.HandleAsync(integrationEvent);
    }

    private async Task<bool> IsAvailableAsync(Guid vehicleId)
    {
        await using var context = fixture.CreateContext();
        return (await context.Vehicles.SingleAsync(v => v.Id == vehicleId)).Available;
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task A_confirmed_rental_blocks_the_vehicle()
    {
        var handled = await HandleAsync(new RentalConfirmedIntegrationEvent(
            Guid.NewGuid(), Guid.NewGuid(), FleetSeed.EconomyVehicleId, 90m, "USD", Now));

        handled.ShouldBeTrue();
        (await IsAvailableAsync(FleetSeed.EconomyVehicleId)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_cancelled_rental_frees_the_vehicle_again()
    {
        await HandleAsync(new RentalConfirmedIntegrationEvent(
            Guid.NewGuid(), Guid.NewGuid(), FleetSeed.CompactVehicleId, 90m, "USD", Now));

        var handled = await HandleAsync(new RentalCancelledIntegrationEvent(
            Guid.NewGuid(), Guid.NewGuid(), FleetSeed.CompactVehicleId, 90m, 100m, "USD", Now));

        handled.ShouldBeTrue();
        (await IsAvailableAsync(FleetSeed.CompactVehicleId)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_completed_rental_frees_the_vehicle_again()
    {
        await HandleAsync(new RentalConfirmedIntegrationEvent(
            Guid.NewGuid(), Guid.NewGuid(), FleetSeed.LuxuryVehicleId, 360m, "USD", Now));

        await HandleAsync(new RentalCompletedIntegrationEvent(
            Guid.NewGuid(), Guid.NewGuid(), FleetSeed.LuxuryVehicleId, 360m, 0, "USD", Now));

        (await IsAvailableAsync(FleetSeed.LuxuryVehicleId)).ShouldBeTrue();
    }

    [Fact]
    public async Task Reprocessing_the_same_event_changes_nothing()
    {
        await HandleAsync(new RentalConfirmedIntegrationEvent(
            Guid.NewGuid(), Guid.NewGuid(), FleetSeed.SuvVehicleId, 180m, "USD", Now));

        var handledAgain = await HandleAsync(new RentalConfirmedIntegrationEvent(
            Guid.NewGuid(), Guid.NewGuid(), FleetSeed.SuvVehicleId, 180m, "USD", Now));

        // Idempotencia: Kafka garantiza "al menos una vez", asi que reprocesar
        // es normal y no debe producir efectos adicionales.
        handledAgain.ShouldBeFalse();
        (await IsAvailableAsync(FleetSeed.SuvVehicleId)).ShouldBeFalse();
    }

    [Fact]
    public async Task An_event_about_an_unknown_vehicle_is_ignored()
    {
        var handled = await HandleAsync(new RentalConfirmedIntegrationEvent(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 90m, "USD", Now));

        handled.ShouldBeFalse();
    }

    [Fact]
    public async Task A_requested_rental_does_not_block_the_vehicle_yet()
    {
        var handled = await HandleAsync(new RentalRequestedIntegrationEvent(
            Guid.NewGuid(), Guid.NewGuid(), FleetSeed.EconomyVehicleId, Now, Now.AddDays(3), 90m, "USD", Now));

        handled.ShouldBeFalse();
        (await IsAvailableAsync(FleetSeed.EconomyVehicleId)).ShouldBeTrue();
    }
}

using Microsoft.EntityFrameworkCore;
using Rentals.Domain.Model;
using Rentals.Infrastructure.Persistence;
using TestSupport;

namespace Rentals.Infrastructure.Tests;

/// <summary>
/// Nivel 3: el adaptador de persistencia contra PostgreSQL real.
/// Lo que se verifica no es la logica de negocio (ya cubierta en el dominio)
/// sino el MAPEO: que los value objects viajen intactos, que el filtro de
/// solapamiento genere el SQL correcto y que la concurrencia se detecte.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EfRentalRepositoryTests(PostgresFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Saves_and_reloads_the_aggregate_preserving_every_value_object()
    {
        var rental = RentalBuilder.A().WithDailyRate(75.55m, "EUR").ForDays(4).Build();

        await using (var context = fixture.CreateContext())
        {
            await new EfRentalRepository(context).AddAsync(rental);
            await context.SaveChangesAsync();
        }

        await using var readContext = fixture.CreateContext();
        var reloaded = await new EfRentalRepository(readContext).GetByIdAsync(rental.Id);

        reloaded.ShouldNotBeNull();
        reloaded.Id.ShouldBe(rental.Id);
        reloaded.CustomerId.ShouldBe(rental.CustomerId);
        reloaded.VehicleId.ShouldBe(rental.VehicleId);
        reloaded.DailyRate.ShouldBe(Money.Of(75.55m, "EUR"));
        reloaded.EstimatedTotal.ShouldBe(Money.Of(302.20m, "EUR"));
        reloaded.Period.ShouldBe(rental.Period);
        reloaded.License.Number.ShouldBe(rental.License.Number);
        reloaded.Status.ShouldBe(RentalStatus.Pending);
    }

    [Fact]
    public async Task Optional_money_columns_stay_null_until_the_rental_ends()
    {
        var rental = RentalBuilder.A().Build();

        await using (var context = fixture.CreateContext())
        {
            await new EfRentalRepository(context).AddAsync(rental);
            await context.SaveChangesAsync();
        }

        await using var readContext = fixture.CreateContext();
        var reloaded = await new EfRentalRepository(readContext).GetByIdAsync(rental.Id);

        reloaded.ShouldNotBeNull();
        reloaded.FinalTotal.ShouldBeNull();
        reloaded.RefundAmount.ShouldBeNull();
    }

    [Fact]
    public async Task Persists_a_state_transition_and_its_amounts()
    {
        var start = FixedClock.DefaultNow.AddDays(10);
        var rental = RentalBuilder.A().From(start).ForDays(3).WithDailyRate(50m).BuildActive();

        await using (var context = fixture.CreateContext())
        {
            await new EfRentalRepository(context).AddAsync(rental);
            await context.SaveChangesAsync();
        }

        await using (var context = fixture.CreateContext())
        {
            var repository = new EfRentalRepository(context);
            var loaded = await repository.GetByIdAsync(rental.Id);
            loaded.ShouldNotBeNull().Complete(loaded.Period.End.AddHours(30));
            await context.SaveChangesAsync();
        }

        await using var readContext = fixture.CreateContext();
        var completed = await new EfRentalRepository(readContext).GetByIdAsync(rental.Id);

        completed.ShouldNotBeNull();
        completed.Status.ShouldBe(RentalStatus.Completed);
        completed.LateDays.ShouldBe(2);
        completed.FinalTotal.ShouldNotBeNull().Amount.ShouldBe(250m);
    }

    [Fact]
    public async Task HasOverlappingRental_is_true_for_an_intersecting_period()
    {
        var vehicleId = VehicleId.New();
        var start = FixedClock.DefaultNow.AddDays(10);
        await StoreAsync(RentalBuilder.A().ForVehicle(vehicleId).From(start).ForDays(5).Build());

        await using var context = fixture.CreateContext();
        var overlaps = await new EfRentalRepository(context)
            .HasOverlappingRentalAsync(vehicleId, start.AddDays(3), start.AddDays(8));

        overlaps.ShouldBeTrue();
    }

    [Fact]
    public async Task HasOverlappingRental_is_false_for_a_back_to_back_period()
    {
        var vehicleId = VehicleId.New();
        var start = FixedClock.DefaultNow.AddDays(10);
        await StoreAsync(RentalBuilder.A().ForVehicle(vehicleId).From(start).ForDays(5).Build());

        await using var context = fixture.CreateContext();
        var overlaps = await new EfRentalRepository(context)
            .HasOverlappingRentalAsync(vehicleId, start.AddDays(5), start.AddDays(8));

        overlaps.ShouldBeFalse();
    }

    [Fact]
    public async Task HasOverlappingRental_ignores_cancelled_rentals()
    {
        var vehicleId = VehicleId.New();
        var start = FixedClock.DefaultNow.AddDays(10);
        await StoreAsync(RentalBuilder.A().ForVehicle(vehicleId).From(start).ForDays(5).BuildCancelled());

        await using var context = fixture.CreateContext();
        var overlaps = await new EfRentalRepository(context)
            .HasOverlappingRentalAsync(vehicleId, start.AddDays(1), start.AddDays(3));

        overlaps.ShouldBeFalse();
    }

    [Fact]
    public async Task HasOverlappingRental_only_looks_at_the_requested_vehicle()
    {
        var start = FixedClock.DefaultNow.AddDays(10);
        await StoreAsync(RentalBuilder.A().ForVehicle(VehicleId.New()).From(start).ForDays(5).Build());

        await using var context = fixture.CreateContext();
        var overlaps = await new EfRentalRepository(context)
            .HasOverlappingRentalAsync(VehicleId.New(), start, start.AddDays(2));

        overlaps.ShouldBeFalse();
    }

    [Fact]
    public async Task ListByCustomer_returns_only_that_customer_newest_first()
    {
        var customerId = CustomerId.New();
        await StoreAsync(RentalBuilder.A().ForCustomer(customerId).Now(FixedClock.DefaultNow).Build());
        await StoreAsync(RentalBuilder.A().ForCustomer(customerId).Now(FixedClock.DefaultNow.AddHours(2)).Build());
        await StoreAsync(RentalBuilder.A().ForCustomer(CustomerId.New()).Build());

        await using var context = fixture.CreateContext();
        var rentals = await new EfRentalRepository(context).ListByCustomerAsync(customerId);

        rentals.Count.ShouldBe(2);
        rentals[0].RequestedAt.ShouldBeGreaterThan(rentals[1].RequestedAt);
    }

    [Fact]
    public async Task GetById_returns_null_for_an_unknown_id()
    {
        await using var context = fixture.CreateContext();

        (await new EfRentalRepository(context).GetByIdAsync(RentalId.New())).ShouldBeNull();
    }

    [Fact]
    public async Task Two_concurrent_updates_of_the_same_rental_make_the_second_one_fail()
    {
        var rental = RentalBuilder.A().Build();
        await StoreAsync(rental);

        await using var firstContext = fixture.CreateContext();
        await using var secondContext = fixture.CreateContext();

        var first = await new EfRentalRepository(firstContext).GetByIdAsync(rental.Id);
        var second = await new EfRentalRepository(secondContext).GetByIdAsync(rental.Id);

        first.ShouldNotBeNull().Confirm(FixedClock.DefaultNow);
        await firstContext.SaveChangesAsync();

        second.ShouldNotBeNull().Cancel(FixedClock.DefaultNow);

        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => secondContext.SaveChangesAsync());
    }

    [Fact]
    public async Task Timestamps_survive_the_round_trip_as_utc()
    {
        var rental = RentalBuilder.A().Build();
        await StoreAsync(rental);

        await using var context = fixture.CreateContext();
        var reloaded = await new EfRentalRepository(context).GetByIdAsync(rental.Id);

        reloaded.ShouldNotBeNull();
        reloaded.Period.Start.Offset.ShouldBe(TimeSpan.Zero);
        reloaded.Period.Start.ShouldBe(rental.Period.Start);
        reloaded.RequestedAt.ShouldBe(rental.RequestedAt);
    }

    private async Task StoreAsync(Rental rental)
    {
        await using var context = fixture.CreateContext();
        await new EfRentalRepository(context).AddAsync(rental);
        await context.SaveChangesAsync();
    }
}

using Rentals.Application.Abstractions;
using Rentals.Domain.Model;
using Shared.Contracts;
using TestSupport;

namespace Rentals.Application.Tests;

/// <summary>
/// Nivel 2: se prueba la ORQUESTACION, no las reglas. Lo que se verifica aqui
/// es a quien se llama, con que argumentos, en que orden y que se devuelve
/// cuando un colaborador falla.
/// </summary>
public sealed class RequestRentalTests
{
    private readonly RentalServiceHarness _harness = new();

    [Fact]
    public async Task Returns_the_created_rental_on_the_happy_path()
    {
        var command = RentalServiceHarness.ValidCommand();

        var result = await _harness.Service.RequestAsync(command);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
        result.Value.Status.ShouldBe(nameof(RentalStatus.Pending));
        result.Value.CustomerId.ShouldBe(command.CustomerId);
        result.Value.VehicleId.ShouldBe(command.VehicleId);
    }

    [Fact]
    public async Task Fails_when_the_vehicle_does_not_exist()
    {
        _harness.VehicleCatalog
            .FindAsync(Arg.Any<VehicleId>(), Arg.Any<CancellationToken>())
            .Returns((VehicleSnapshot?)null);

        var result = await _harness.Service.RequestAsync(RentalServiceHarness.ValidCommand());

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("vehicle.not_found");
        await _harness.Repository.DidNotReceive().AddAsync(Arg.Any<Rental>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fails_when_the_vehicle_is_not_available()
    {
        _harness.VehicleCatalog
            .FindAsync(Arg.Any<VehicleId>(), Arg.Any<CancellationToken>())
            .Returns(TestData.UnavailableVehicle());

        var result = await _harness.Service.RequestAsync(RentalServiceHarness.ValidCommand());

        result.Error.Code.ShouldBe("vehicle.unavailable");
    }

    [Fact]
    public async Task Fails_when_another_rental_overlaps_the_period()
    {
        _harness.Repository
            .HasOverlappingRentalAsync(
                Arg.Any<VehicleId>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _harness.Service.RequestAsync(RentalServiceHarness.ValidCommand());

        result.Error.Code.ShouldBe("rental.overlapping");
        await _harness.PricingCalculator.DidNotReceive()
            .QuoteAsync(Arg.Any<PricingRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Asks_pricing_for_the_class_and_the_billable_days_of_the_period()
    {
        var command = RentalServiceHarness.ValidCommand(
            start: FixedClock.DefaultNow.AddDays(10),
            end: FixedClock.DefaultNow.AddDays(17));

        await _harness.Service.RequestAsync(command);

        await _harness.PricingCalculator.Received(1).QuoteAsync(
            Arg.Is<PricingRequest>(request =>
                request.VehicleClass == "economy"
                && request.Days == 7
                && request.BaseDailyRate == 30m
                && request.Currency == "USD"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Uses_the_daily_rate_returned_by_pricing_and_not_the_catalog_one()
    {
        _harness.PricingCalculator
            .QuoteAsync(Arg.Any<PricingRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PricingQuote(99m, 297m, "USD", []));

        var result = await _harness.Service.RequestAsync(
            RentalServiceHarness.ValidCommand(
                start: FixedClock.DefaultNow.AddDays(10),
                end: FixedClock.DefaultNow.AddDays(13)));

        result.Value.ShouldNotBeNull();
        result.Value.DailyRate.ShouldBe(99m);
        result.Value.EstimatedTotal.ShouldBe(297m);
    }

    [Fact]
    public async Task Persists_and_commits_before_publishing()
    {
        await _harness.Service.RequestAsync(RentalServiceHarness.ValidCommand());

        // El orden importa: publicar antes del commit permitiria notificar una
        // renta que despues no se guardo.
        Received.InOrder(() =>
        {
            _harness.Repository.AddAsync(Arg.Any<Rental>(), Arg.Any<CancellationToken>());
            _harness.UnitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>());
            _harness.EventPublisher.PublishAsync(
                Arg.Any<IReadOnlyCollection<IIntegrationEvent>>(),
                Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Publishes_a_rental_requested_integration_event()
    {
        var command = RentalServiceHarness.ValidCommand();

        await _harness.Service.RequestAsync(command);

        var published = _harness.PublishedEvents().ShouldHaveSingleItem();
        published.EventType.ShouldBe(IntegrationEventTypes.RentalRequested);
        published.ShouldBeOfType<RentalRequestedIntegrationEvent>().CustomerId.ShouldBe(command.CustomerId);
    }

    [Fact]
    public async Task Reports_pricing_unavailable_instead_of_leaking_the_exception()
    {
        _harness.PricingCalculator
            .QuoteAsync(Arg.Any<PricingRequest>(), Arg.Any<CancellationToken>())
            .Returns<PricingQuote>(_ => throw new ExternalServiceUnavailableException("pricing"));

        var result = await _harness.Service.RequestAsync(RentalServiceHarness.ValidCommand());

        result.Error.Code.ShouldBe("pricing.unavailable");
        await _harness.UnitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reports_fleet_unavailable_instead_of_leaking_the_exception()
    {
        _harness.VehicleCatalog
            .FindAsync(Arg.Any<VehicleId>(), Arg.Any<CancellationToken>())
            .Returns<VehicleSnapshot?>(_ => throw new ExternalServiceUnavailableException("fleet"));

        var result = await _harness.Service.RequestAsync(RentalServiceHarness.ValidCommand());

        result.Error.Code.ShouldBe("fleet.unavailable");
    }

    [Fact]
    public async Task Translates_a_domain_rule_violation_into_a_failed_result()
    {
        var command = RentalServiceHarness.ValidCommand(
            start: FixedClock.DefaultNow.AddDays(10),
            end: FixedClock.DefaultNow.AddDays(20),
            licenseExpiry: FixedClock.DefaultNow.AddDays(15));

        var result = await _harness.Service.RequestAsync(command);

        result.Error.Code.ShouldBe("rental.license_expired");
    }

    [Fact]
    public async Task Rejects_an_invalid_period_before_touching_any_collaborator()
    {
        var command = RentalServiceHarness.ValidCommand(
            start: FixedClock.DefaultNow.AddDays(10),
            end: FixedClock.DefaultNow.AddDays(10));

        var result = await _harness.Service.RequestAsync(command);

        result.Error.Code.ShouldBe("rental.invalid_period");
        await _harness.VehicleCatalog.DidNotReceive().FindAsync(Arg.Any<VehicleId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Keeps_the_rental_when_publishing_fails_because_the_commit_already_happened()
    {
        _harness.EventPublisher
            .PublishAsync(Arg.Any<IReadOnlyCollection<IIntegrationEvent>>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("broker down"));

        var result = await _harness.Service.RequestAsync(RentalServiceHarness.ValidCommand());

        result.IsSuccess.ShouldBeTrue();
        await _harness.UnitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stamps_the_rental_with_the_time_given_by_the_clock_port()
    {
        _harness.Clock.SetTo(new DateTimeOffset(2026, 7, 4, 8, 30, 0, TimeSpan.Zero));

        await _harness.Service.RequestAsync(
            RentalServiceHarness.ValidCommand(
                start: new DateTimeOffset(2026, 7, 10, 9, 0, 0, TimeSpan.Zero),
                end: new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero)));

        var published = _harness.PublishedEvents().ShouldHaveSingleItem();
        published.OccurredAt.ShouldBe(new DateTimeOffset(2026, 7, 4, 8, 30, 0, TimeSpan.Zero));
    }
}

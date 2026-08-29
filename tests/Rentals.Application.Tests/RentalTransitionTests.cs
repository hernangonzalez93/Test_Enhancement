using Rentals.Domain.Model;
using Shared.Contracts;
using TestSupport;

namespace Rentals.Application.Tests;

public sealed class RentalTransitionTests
{
    private readonly RentalServiceHarness _harness = new();

    [Fact]
    public async Task Confirm_saves_and_publishes_the_confirmation()
    {
        var rental = RentalBuilder.A().Build();
        _harness.GivenStoredRental(rental);

        var result = await _harness.Service.ConfirmAsync(rental.Id.Value);

        result.Value.ShouldNotBeNull().Status.ShouldBe(nameof(RentalStatus.Confirmed));
        await _harness.UnitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        _harness.PublishedEvents().ShouldContain(e => e.EventType == IntegrationEventTypes.RentalConfirmed);
    }

    [Fact]
    public async Task Confirm_returns_not_found_when_the_rental_does_not_exist()
    {
        var result = await _harness.Service.ConfirmAsync(Guid.CreateVersion7());

        result.Error.Code.ShouldBe("rental.not_found");
        await _harness.UnitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Confirm_on_an_already_confirmed_rental_fails_without_saving()
    {
        var rental = RentalBuilder.A().BuildConfirmed();
        _harness.GivenStoredRental(rental);

        var result = await _harness.Service.ConfirmAsync(rental.Id.Value);

        result.Error.Code.ShouldBe("rental.invalid_state");
        await _harness.UnitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancel_publishes_the_refund_computed_by_the_domain()
    {
        var rental = RentalBuilder.A().WithDailyRate(50m).ForDays(3).BuildConfirmed();
        _harness.GivenStoredRental(rental);

        var result = await _harness.Service.CancelAsync(rental.Id.Value);

        result.Value.ShouldNotBeNull().RefundAmount.ShouldBe(150m);
        var cancelled = _harness.PublishedEvents()
            .OfType<RentalCancelledIntegrationEvent>()
            .ShouldHaveSingleItem();
        cancelled.RefundAmount.ShouldBe(150m);
        cancelled.RefundPercentage.ShouldBe(100m);
    }

    [Fact]
    public async Task Start_moves_the_rental_to_active_using_the_clock()
    {
        var start = FixedClock.DefaultNow.AddDays(10);
        var rental = RentalBuilder.A().From(start).ForDays(3).BuildConfirmed();
        _harness.GivenStoredRental(rental);
        _harness.Clock.SetTo(start);

        var result = await _harness.Service.StartAsync(rental.Id.Value);

        result.Value.ShouldNotBeNull().Status.ShouldBe(nameof(RentalStatus.Active));
        _harness.PublishedEvents().ShouldContain(e => e.EventType == IntegrationEventTypes.RentalStarted);
    }

    [Fact]
    public async Task Start_before_the_pickup_time_fails()
    {
        var start = FixedClock.DefaultNow.AddDays(10);
        var rental = RentalBuilder.A().From(start).ForDays(3).BuildConfirmed();
        _harness.GivenStoredRental(rental);
        _harness.Clock.SetTo(start.AddHours(-1));

        var result = await _harness.Service.StartAsync(rental.Id.Value);

        result.Error.Code.ShouldBe("rental.not_startable_yet");
    }

    [Fact]
    public async Task Complete_publishes_the_final_total_including_the_late_surcharge()
    {
        var start = FixedClock.DefaultNow.AddDays(10);
        var rental = RentalBuilder.A().From(start).ForDays(3).WithDailyRate(50m).BuildActive();
        _harness.GivenStoredRental(rental);
        _harness.Clock.SetTo(rental.Period.End.AddHours(30));

        var result = await _harness.Service.CompleteAsync(rental.Id.Value);

        result.Value.ShouldNotBeNull().FinalTotal.ShouldBe(250m);
        var completed = _harness.PublishedEvents()
            .OfType<RentalCompletedIntegrationEvent>()
            .ShouldHaveSingleItem();
        completed.LateDays.ShouldBe(2);
        completed.FinalTotal.ShouldBe(250m);
    }

    [Fact]
    public async Task Extend_moves_the_end_recomputes_the_total_and_publishes_it()
    {
        var start = FixedClock.DefaultNow.AddDays(10);
        var rental = RentalBuilder.A().From(start).ForDays(3).WithDailyRate(50m).BuildConfirmed();
        _harness.GivenStoredRental(rental);

        var result = await _harness.Service.ExtendAsync(rental.Id.Value, start.AddDays(5));

        result.Value.ShouldNotBeNull().TotalDays.ShouldBe(5);
        result.Value.EstimatedTotal.ShouldBe(250m);
        _harness.PublishedEvents().ShouldContain(e => e.EventType == IntegrationEventTypes.RentalExtended);
    }

    [Fact]
    public async Task Extend_backwards_is_rejected_without_saving()
    {
        var start = FixedClock.DefaultNow.AddDays(10);
        var rental = RentalBuilder.A().From(start).ForDays(3).BuildConfirmed();
        _harness.GivenStoredRental(rental);

        var result = await _harness.Service.ExtendAsync(rental.Id.Value, start.AddDays(1));

        result.Error.Code.ShouldBe("rental.invalid_period");
        await _harness.UnitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_maps_the_aggregate_into_a_dto()
    {
        var rental = RentalBuilder.A().WithDailyRate(60m).ForDays(2).Build();
        _harness.GivenStoredRental(rental);

        var result = await _harness.Service.GetAsync(rental.Id.Value);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
        result.Value.Id.ShouldBe(rental.Id.Value);
        result.Value.TotalDays.ShouldBe(2);
        result.Value.EstimatedTotal.ShouldBe(120m);
        result.Value.FinalTotal.ShouldBeNull();
    }

    [Fact]
    public async Task Get_returns_not_found_for_an_unknown_id()
    {
        var result = await _harness.Service.GetAsync(Guid.CreateVersion7());

        result.Error.Code.ShouldBe("rental.not_found");
    }

    [Fact]
    public async Task List_returns_every_rental_of_the_customer()
    {
        var customerId = CustomerId.New();
        var rentals = new[]
        {
            RentalBuilder.A().ForCustomer(customerId).Build(),
            RentalBuilder.A().ForCustomer(customerId).Build()
        };

        _harness.Repository
            .ListByCustomerAsync(customerId, Arg.Any<CancellationToken>())
            .Returns(rentals);

        var result = await _harness.Service.ListByCustomerAsync(customerId.Value);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull().Count.ShouldBe(2);
    }
}

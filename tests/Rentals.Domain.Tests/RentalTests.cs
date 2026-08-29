using Rentals.Domain.Events;
using Rentals.Domain.Exceptions;
using Rentals.Domain.Model;
using TestSupport;

namespace Rentals.Domain.Tests;

/// <summary>
/// El agregado es donde vive la maquina de estados. Estas pruebas cubren cada
/// transicion legal y, sobre todo, cada transicion ilegal: un dominio bien
/// probado se reconoce por cuantos "no puedes hacer eso" tiene cubiertos.
/// </summary>
public sealed class RentalTests
{
    private static readonly DateTimeOffset Now = FixedClock.DefaultNow;

    [Fact]
    public void Request_creates_the_rental_in_pending_state()
    {
        var rental = RentalBuilder.A().Build();

        rental.Status.ShouldBe(RentalStatus.Pending);
        rental.RequestedAt.ShouldBe(Now);
        rental.ConfirmedAt.ShouldBeNull();
    }

    [Fact]
    public void Request_computes_the_estimated_total_from_the_daily_rate()
    {
        var rental = RentalBuilder.A().WithDailyRate(45m).ForDays(4).Build();

        rental.Period.TotalDays.ShouldBe(4);
        rental.EstimatedTotal.Amount.ShouldBe(180m);
    }

    [Fact]
    public void Request_raises_a_rental_requested_event()
    {
        var rental = RentalBuilder.A().WithDailyRate(50m).ForDays(3).Build();

        var @event = rental.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<RentalRequested>();
        @event.RentalId.ShouldBe(rental.Id);
        @event.EstimatedTotal.Amount.ShouldBe(150m);
        @event.OccurredAt.ShouldBe(Now);
    }

    [Fact]
    public void Request_rejects_a_period_that_already_started()
    {
        var exception = Should.Throw<RentalPeriodInThePastException>(
            () => RentalBuilder.A().From(Now.AddDays(-1)).To(Now.AddDays(2)).Build());

        exception.Code.ShouldBe("rental.period_in_the_past");
    }

    [Fact]
    public void Request_accepts_a_period_starting_exactly_now()
    {
        var rental = RentalBuilder.A().From(Now).To(Now.AddDays(2)).Build();

        rental.Status.ShouldBe(RentalStatus.Pending);
    }

    [Fact]
    public void Request_rejects_a_license_expiring_before_the_return()
    {
        Should.Throw<DriverLicenseExpiredException>(
            () => RentalBuilder.A()
                .From(Now.AddDays(10))
                .To(Now.AddDays(20))
                .WithLicenseExpiringOn(Now.AddDays(15))
                .Build());
    }

    [Fact]
    public void Confirm_moves_a_pending_rental_to_confirmed()
    {
        var rental = RentalBuilder.A().Build();
        rental.ClearDomainEvents();

        rental.Confirm(Now);

        rental.Status.ShouldBe(RentalStatus.Confirmed);
        rental.ConfirmedAt.ShouldBe(Now);
        rental.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<RentalConfirmed>();
    }

    [Fact]
    public void Confirm_twice_is_rejected()
    {
        var rental = RentalBuilder.A().BuildConfirmed();

        var exception = Should.Throw<InvalidRentalStateException>(() => rental.Confirm(Now));

        exception.CurrentState.ShouldBe(nameof(RentalStatus.Confirmed));
        exception.AttemptedTransition.ShouldBe("confirm");
    }

    [Fact]
    public void Cancel_from_pending_refunds_nothing_because_nothing_was_charged()
    {
        var rental = RentalBuilder.A().Build();

        rental.Cancel(Now);

        rental.Status.ShouldBe(RentalStatus.Cancelled);
        rental.RefundAmount.ShouldNotBeNull().IsZero.ShouldBeTrue();
    }

    [Fact]
    public void Cancel_from_confirmed_refunds_according_to_the_policy()
    {
        // Inicio dentro de 10 dias, se cancela ahora: mas de 48 horas de antelacion.
        var rental = RentalBuilder.A().WithDailyRate(50m).ForDays(3).BuildConfirmed();
        rental.ClearDomainEvents();

        rental.Cancel(Now);

        rental.RefundAmount.ShouldNotBeNull().Amount.ShouldBe(150m);
        var @event = rental.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<RentalCancelled>();
        @event.RefundPercentage.ShouldBe(100m);
    }

    [Fact]
    public void Cancel_within_a_day_of_the_start_refunds_only_a_quarter()
    {
        var start = Now.AddDays(10);
        var rental = RentalBuilder.A().From(start).ForDays(2).WithDailyRate(100m).BuildConfirmed();

        rental.Cancel(start.AddHours(-3));

        rental.RefundAmount.ShouldNotBeNull().Amount.ShouldBe(50m);
    }

    [Fact]
    public void Cancel_from_pending_owes_nothing_because_nothing_was_charged()
    {
        // Distincion importante para facturacion: cancelar antes de confirmar
        // no genera cargo. Sin ella, Billing facturaria la renta entera.
        var rental = RentalBuilder.A().WithDailyRate(50m).ForDays(3).Build();

        rental.Cancel(Now);

        var @event = rental.DomainEvents.OfType<RentalCancelled>().Single();
        @event.RefundAmount.IsZero.ShouldBeTrue();
        @event.PenaltyAmount.IsZero.ShouldBeTrue();
    }

    [Theory]
    [InlineData(240, 150, 0)]     // mas de 48 h: reembolso total, nada que cobrar
    [InlineData(30, 75, 75)]      // entre 24 y 48 h: mitad y mitad
    [InlineData(3, 37.5, 112.5)]  // menos de 24 h: se cobra el 75 %
    [InlineData(0, 0, 150)]       // ya iniciada: se cobra todo
    public void Cancel_from_confirmed_charges_what_is_not_refunded(
        int hoursAhead,
        decimal expectedRefund,
        decimal expectedPenalty)
    {
        var start = Now.AddDays(10);
        var rental = RentalBuilder.A().From(start).ForDays(3).WithDailyRate(50m).BuildConfirmed();
        rental.ClearDomainEvents();

        rental.Cancel(start.AddHours(-hoursAhead));

        var @event = rental.DomainEvents.OfType<RentalCancelled>().Single();
        @event.RefundAmount.Amount.ShouldBe(expectedRefund);
        @event.PenaltyAmount.Amount.ShouldBe(expectedPenalty);
        // Lo devuelto mas lo cobrado siempre suma el total estimado.
        (@event.RefundAmount.Amount + @event.PenaltyAmount.Amount).ShouldBe(150m);
    }

    [Fact]
    public void Cancel_is_rejected_once_the_vehicle_was_picked_up()
    {
        var rental = RentalBuilder.A().BuildActive();

        Should.Throw<InvalidRentalStateException>(() => rental.Cancel(Now.AddDays(11)));
    }

    [Fact]
    public void Cancel_is_rejected_on_a_completed_rental()
    {
        var rental = RentalBuilder.A().BuildCompleted();

        Should.Throw<InvalidRentalStateException>(() => rental.Cancel(Now.AddDays(20)));
    }

    [Fact]
    public void Cancel_twice_is_rejected()
    {
        var rental = RentalBuilder.A().BuildCancelled();

        Should.Throw<InvalidRentalStateException>(() => rental.Cancel(Now));
    }

    [Fact]
    public void Start_moves_a_confirmed_rental_to_active()
    {
        var start = Now.AddDays(10);
        var rental = RentalBuilder.A().From(start).ForDays(3).BuildConfirmed();
        rental.ClearDomainEvents();

        rental.Start(start);

        rental.Status.ShouldBe(RentalStatus.Active);
        rental.StartedAt.ShouldBe(start);
        rental.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<RentalStarted>();
    }

    [Fact]
    public void Start_before_the_agreed_time_is_rejected()
    {
        var start = Now.AddDays(10);
        var rental = RentalBuilder.A().From(start).ForDays(3).BuildConfirmed();

        var exception = Should.Throw<RentalNotStartableYetException>(() => rental.Start(start.AddMinutes(-1)));

        exception.Code.ShouldBe("rental.not_startable_yet");
    }

    [Fact]
    public void Start_is_rejected_on_a_rental_that_was_never_confirmed()
    {
        var rental = RentalBuilder.A().Build();

        Should.Throw<InvalidRentalStateException>(() => rental.Start(Now.AddDays(10)));
    }

    [Fact]
    public void Complete_on_time_charges_exactly_the_estimated_total()
    {
        var start = Now.AddDays(10);
        var rental = RentalBuilder.A().From(start).ForDays(3).WithDailyRate(50m).BuildActive();
        rental.ClearDomainEvents();

        rental.Complete(rental.Period.End);

        rental.Status.ShouldBe(RentalStatus.Completed);
        rental.LateDays.ShouldBe(0);
        rental.FinalTotal.ShouldNotBeNull().Amount.ShouldBe(150m);
    }

    [Fact]
    public void Complete_ahead_of_time_does_not_produce_a_discount()
    {
        var start = Now.AddDays(10);
        var rental = RentalBuilder.A().From(start).ForDays(3).WithDailyRate(50m).BuildActive();

        rental.Complete(rental.Period.End.AddDays(-1));

        rental.FinalTotal.ShouldNotBeNull().Amount.ShouldBe(150m);
        rental.LateDays.ShouldBe(0);
    }

    [Theory]
    [InlineData(1, 1, 200)]     // una hora tarde ya cuenta como un dia
    [InlineData(24, 1, 200)]
    [InlineData(25, 2, 250)]
    [InlineData(72, 3, 300)]
    public void Complete_late_charges_one_extra_day_per_started_block(int lateHours, int expectedLateDays, decimal expectedTotal)
    {
        var start = Now.AddDays(10);
        var rental = RentalBuilder.A().From(start).ForDays(3).WithDailyRate(50m).BuildActive();

        rental.Complete(rental.Period.End.AddHours(lateHours));

        rental.LateDays.ShouldBe(expectedLateDays);
        rental.FinalTotal.ShouldNotBeNull().Amount.ShouldBe(expectedTotal);
    }

    [Fact]
    public void Complete_raises_an_event_carrying_the_final_total()
    {
        var start = Now.AddDays(10);
        var rental = RentalBuilder.A().From(start).ForDays(2).WithDailyRate(40m).BuildActive();
        rental.ClearDomainEvents();

        rental.Complete(rental.Period.End.AddHours(30));

        var @event = rental.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<RentalCompleted>();
        @event.LateDays.ShouldBe(2);
        @event.FinalTotal.Amount.ShouldBe(160m);
    }

    [Fact]
    public void Complete_is_rejected_on_a_rental_that_was_not_picked_up()
    {
        var rental = RentalBuilder.A().BuildConfirmed();

        Should.Throw<InvalidRentalStateException>(() => rental.Complete(Now.AddDays(13)));
    }

    [Fact]
    public void Extend_moves_the_end_and_recomputes_the_estimated_total()
    {
        var start = Now.AddDays(10);
        var rental = RentalBuilder.A().From(start).ForDays(3).WithDailyRate(50m).BuildConfirmed();
        rental.ClearDomainEvents();

        rental.Extend(start.AddDays(5), Now);

        rental.Period.TotalDays.ShouldBe(5);
        rental.EstimatedTotal.Amount.ShouldBe(250m);
        rental.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<RentalExtended>();
    }

    [Fact]
    public void Extend_leaves_the_rental_untouched_when_the_license_does_not_cover_the_new_end()
    {
        var start = Now.AddDays(10);
        var rental = RentalBuilder.A()
            .From(start)
            .ForDays(3)
            .WithDailyRate(50m)
            .WithLicenseExpiringOn(start.AddDays(4))
            .BuildConfirmed();

        Should.Throw<DriverLicenseExpiredException>(() => rental.Extend(start.AddDays(10), Now));

        rental.Period.TotalDays.ShouldBe(3);
        rental.EstimatedTotal.Amount.ShouldBe(150m);
    }

    [Fact]
    public void Extend_is_rejected_on_a_completed_rental()
    {
        var rental = RentalBuilder.A().BuildCompleted();

        Should.Throw<InvalidRentalStateException>(() => rental.Extend(rental.Period.End.AddDays(2), Now));
    }

    [Fact]
    public void ClearDomainEvents_empties_the_pending_events()
    {
        var rental = RentalBuilder.A().Build();
        rental.DomainEvents.ShouldNotBeEmpty();

        rental.ClearDomainEvents();

        rental.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void The_full_happy_path_accumulates_one_event_per_transition()
    {
        var start = Now.AddDays(10);
        var rental = RentalBuilder.A().From(start).ForDays(3).Build();

        rental.Confirm(Now);
        rental.Start(start);
        rental.Complete(rental.Period.End);

        rental.DomainEvents.Select(e => e.GetType()).ShouldBe(
        [
            typeof(RentalRequested),
            typeof(RentalConfirmed),
            typeof(RentalStarted),
            typeof(RentalCompleted)
        ]);
    }
}

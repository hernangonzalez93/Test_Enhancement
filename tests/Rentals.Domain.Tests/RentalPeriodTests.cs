using Rentals.Domain.Exceptions;
using Rentals.Domain.Model;
using TestSupport;

namespace Rentals.Domain.Tests;

public sealed class RentalPeriodTests
{
    private static readonly DateTimeOffset Base = FixedClock.DefaultNow;

    [Fact]
    public void Create_normalizes_both_ends_to_utc()
    {
        var start = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.FromHours(-5));

        var period = RentalPeriod.Create(start, start.AddDays(2));

        period.Start.Offset.ShouldBe(TimeSpan.Zero);
        period.End.Offset.ShouldBe(TimeSpan.Zero);
        period.Start.UtcDateTime.Hour.ShouldBe(17);
    }

    [Fact]
    public void Create_rejects_an_end_before_the_start()
    {
        Should.Throw<InvalidRentalPeriodException>(() => RentalPeriod.Create(Base, Base.AddHours(-1)));
    }

    [Fact]
    public void Create_rejects_an_end_equal_to_the_start()
    {
        Should.Throw<InvalidRentalPeriodException>(() => RentalPeriod.Create(Base, Base));
    }

    [Fact]
    public void Create_accepts_exactly_the_maximum_length()
    {
        var period = RentalPeriod.Create(Base, Base.AddDays(RentalPeriod.MaxDays));

        period.TotalDays.ShouldBe(RentalPeriod.MaxDays);
    }

    [Fact]
    public void Create_rejects_a_period_longer_than_the_maximum()
    {
        var exception = Should.Throw<InvalidRentalPeriodException>(
            () => RentalPeriod.Create(Base, Base.AddDays(RentalPeriod.MaxDays).AddHours(1)));

        exception.Code.ShouldBe("rental.invalid_period");
    }

    [Theory]
    [InlineData(1, 1)]      // una hora ya factura un dia completo
    [InlineData(23, 1)]
    [InlineData(24, 1)]
    [InlineData(25, 2)]     // cualquier fraccion extra suma otro dia
    [InlineData(48, 2)]
    [InlineData(72, 3)]
    public void TotalDays_bills_every_started_block_of_24_hours(int hours, int expectedDays)
    {
        RentalPeriod.Create(Base, Base.AddHours(hours)).TotalDays.ShouldBe(expectedDays);
    }

    [Fact]
    public void Contains_uses_a_half_open_interval()
    {
        var period = RentalPeriod.Create(Base, Base.AddDays(2));

        period.Contains(Base).ShouldBeTrue();
        period.Contains(Base.AddDays(1)).ShouldBeTrue();
        period.Contains(period.End).ShouldBeFalse();
        period.Contains(Base.AddSeconds(-1)).ShouldBeFalse();
    }

    [Fact]
    public void Overlaps_detects_a_partial_intersection()
    {
        var first = RentalPeriod.Create(Base, Base.AddDays(5));
        var second = RentalPeriod.Create(Base.AddDays(3), Base.AddDays(8));

        first.Overlaps(second).ShouldBeTrue();
        second.Overlaps(first).ShouldBeTrue();
    }

    [Fact]
    public void Overlaps_is_false_for_back_to_back_periods()
    {
        var first = RentalPeriod.Create(Base, Base.AddDays(5));
        var second = RentalPeriod.Create(Base.AddDays(5), Base.AddDays(8));

        first.Overlaps(second).ShouldBeFalse();
    }

    [Fact]
    public void Overlaps_is_true_when_one_period_contains_the_other()
    {
        var outer = RentalPeriod.Create(Base, Base.AddDays(10));
        var inner = RentalPeriod.Create(Base.AddDays(2), Base.AddDays(4));

        outer.Overlaps(inner).ShouldBeTrue();
    }

    [Fact]
    public void IsInThePast_compares_the_start_against_the_given_instant()
    {
        var period = RentalPeriod.Create(Base.AddDays(1), Base.AddDays(3));

        period.IsInThePast(Base).ShouldBeFalse();
        period.IsInThePast(Base.AddDays(2)).ShouldBeTrue();
    }

    [Fact]
    public void ExtendTo_moves_the_end_forward_and_recomputes_the_days()
    {
        var period = RentalPeriod.Create(Base, Base.AddDays(3));

        var extended = period.ExtendTo(Base.AddDays(5));

        extended.Start.ShouldBe(period.Start);
        extended.TotalDays.ShouldBe(5);
    }

    [Fact]
    public void ExtendTo_rejects_an_end_that_does_not_move_forward()
    {
        var period = RentalPeriod.Create(Base, Base.AddDays(3));

        Should.Throw<InvalidRentalPeriodException>(() => period.ExtendTo(Base.AddDays(3)));
    }

    [Fact]
    public void Two_periods_with_the_same_instants_are_equal()
    {
        RentalPeriod.Create(Base, Base.AddDays(2))
            .ShouldBe(RentalPeriod.Create(Base, Base.AddDays(2)));
    }
}

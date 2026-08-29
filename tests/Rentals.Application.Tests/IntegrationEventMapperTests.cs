using Rentals.Application.Rentals;
using Rentals.Domain.Events;
using Rentals.Domain.Model;
using Shared.Contracts;
using TestSupport;

namespace Rentals.Application.Tests;

/// <summary>
/// La frontera entre el lenguaje interno (eventos de dominio) y el contrato
/// publico (eventos de integracion). Si esta traduccion se rompe, Fleet y
/// Notifications dejan de entender los mensajes sin que nada mas falle.
/// </summary>
public sealed class IntegrationEventMapperTests
{
    [Fact]
    public void Maps_a_requested_event_flattening_the_value_objects()
    {
        var rental = RentalBuilder.A().WithDailyRate(50m).ForDays(3).Build();
        var domainEvent = rental.DomainEvents.OfType<RentalRequested>().Single();

        var mapped = IntegrationEventMapper.Map(domainEvent)
            .ShouldBeOfType<RentalRequestedIntegrationEvent>();

        mapped.RentalId.ShouldBe(rental.Id.Value);
        mapped.EstimatedTotal.ShouldBe(150m);
        mapped.Currency.ShouldBe("USD");
        mapped.EventType.ShouldBe(IntegrationEventTypes.RentalRequested);
    }

    [Fact]
    public void Uses_the_rental_id_as_partition_key_to_preserve_ordering()
    {
        var rental = RentalBuilder.A().Build();
        var mapped = IntegrationEventMapper.Map(rental.DomainEvents.Single());

        mapped.ShouldNotBeNull().PartitionKey.ShouldBe(rental.Id.Value.ToString());
    }

    [Fact]
    public void Does_not_publish_the_internal_extended_event()
    {
        var rental = RentalBuilder.A().BuildConfirmed();
        rental.ClearDomainEvents();
        rental.Extend(rental.Period.End.AddDays(2), FixedClock.DefaultNow);

        IntegrationEventMapper.Map(rental.DomainEvents).ShouldBeEmpty();
    }

    [Fact]
    public void Maps_every_publishable_event_of_the_happy_path()
    {
        var start = FixedClock.DefaultNow.AddDays(10);
        var rental = RentalBuilder.A().From(start).ForDays(3).Build();
        rental.Confirm(FixedClock.DefaultNow);
        rental.Start(start);
        rental.Complete(rental.Period.End);

        var mapped = IntegrationEventMapper.Map(rental.DomainEvents);

        mapped.Select(e => e.EventType).ShouldBe(
        [
            IntegrationEventTypes.RentalRequested,
            IntegrationEventTypes.RentalConfirmed,
            IntegrationEventTypes.RentalStarted,
            IntegrationEventTypes.RentalCompleted
        ]);
    }

    [Fact]
    public void Maps_a_cancellation_carrying_amount_and_percentage()
    {
        var rental = RentalBuilder.A().WithDailyRate(100m).ForDays(2).BuildConfirmed();
        rental.ClearDomainEvents();
        rental.Cancel(rental.Period.Start.AddHours(-30));

        var mapped = IntegrationEventMapper.Map(rental.DomainEvents.Single())
            .ShouldBeOfType<RentalCancelledIntegrationEvent>();

        mapped.RefundAmount.ShouldBe(100m);
        mapped.RefundPercentage.ShouldBe(50m);
    }
}

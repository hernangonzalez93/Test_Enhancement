using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Notifications.Api;
using Shared.Contracts;

namespace Notifications.Tests;

/// <summary>
/// Este proyecto usa Moq en lugar de NSubstitute a proposito: el stack de
/// pruebas del repositorio muestra las dos bibliotecas para poder compararlas.
/// La diferencia es puramente de sintaxis; el patron (arrange - act - verify)
/// es identico.
/// </summary>
public sealed class NotificationIngestorTests
{
    private readonly Mock<INotificationStore> _store = new(MockBehavior.Strict);

    private NotificationIngestor CreateSut() =>
        new(_store.Object, NullLogger<NotificationIngestor>.Instance);

    private static RentalConfirmedIntegrationEvent ConfirmedEvent(Guid? rentalId = null, Guid? customerId = null) =>
        new(
            rentalId ?? Guid.NewGuid(),
            customerId ?? Guid.NewGuid(),
            Guid.NewGuid(),
            150m,
            "USD",
            DateTimeOffset.UtcNow);

    [Fact]
    public async Task A_confirmed_event_produces_exactly_one_stored_notification()
    {
        Notification? captured = null;
        _store
            .Setup(store => store.AddAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()))
            .Callback<Notification, CancellationToken>((notification, _) => captured = notification)
            .ReturnsAsync(true);

        var @event = ConfirmedEvent();

        var ingested = await CreateSut().IngestAsync(@event);

        ingested.ShouldBeTrue();
        _store.Verify(store => store.AddAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()), Times.Once);
        captured.ShouldNotBeNull();
        captured.RentalId.ShouldBe(@event.RentalId);
        captured.CustomerId.ShouldBe(@event.CustomerId);
        captured.EventType.ShouldBe(IntegrationEventTypes.RentalConfirmed);
    }

    [Fact]
    public async Task Reprocessing_the_same_event_stores_a_single_notification()
    {
        // Con el almacen real, no un doble: lo que se prueba es la deduplicacion
        // de punta a punta entre la factoria, el ingestor y el almacen.
        var store = new InMemoryNotificationStore();
        var sut = new NotificationIngestor(store, NullLogger<NotificationIngestor>.Instance);
        var @event = ConfirmedEvent();

        var first = await sut.IngestAsync(@event);
        var second = await sut.IngestAsync(@event);
        var third = await sut.IngestAsync(@event);

        first.ShouldBeTrue();
        second.ShouldBeFalse();
        third.ShouldBeFalse();
        (await store.ListAsync(@event.CustomerId)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_duplicate_rejected_by_the_store_is_reported_as_not_ingested()
    {
        _store
            .Setup(store => store.AddAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        (await CreateSut().IngestAsync(ConfirmedEvent())).ShouldBeFalse();
    }

    [Fact]
    public async Task An_unknown_event_stores_nothing()
    {
        var ingested = await CreateSut().IngestAsync(new UnknownEvent());

        ingested.ShouldBeFalse();
        _store.Verify(store => store.AddAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_null_event_is_rejected()
    {
        await Should.ThrowAsync<ArgumentNullException>(() => CreateSut().IngestAsync(null!));
    }

    private sealed record UnknownEvent : IIntegrationEvent
    {
        public Guid EventId => Guid.NewGuid();

        public string EventType => "rental.teleported";

        public DateTimeOffset OccurredAt => DateTimeOffset.UtcNow;

        public string PartitionKey => "n/a";
    }
}

/// <summary>Traduccion de eventos a texto para el cliente. Funcion pura.</summary>
public sealed class NotificationFactoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_requested_event_mentions_the_estimated_total()
    {
        var notification = NotificationFactory.From(
            new RentalRequestedIntegrationEvent(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now, Now.AddDays(3), 150m, "USD", Now));

        notification.ShouldNotBeNull();
        notification.Message.ShouldContain("150.00 USD");
        notification.CreatedAt.ShouldBe(Now);
    }

    [Fact]
    public void A_cancelled_event_mentions_the_refund_and_its_percentage()
    {
        var notification = NotificationFactory.From(
            new RentalCancelledIntegrationEvent(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 150m, 75m, 75m, 50m, "USD", Now));

        notification.ShouldNotBeNull();
        notification.Message.ShouldContain("75.00 USD");
        notification.Message.ShouldContain("50%");
    }

    [Fact]
    public void A_completed_event_on_time_does_not_mention_late_days()
    {
        var notification = NotificationFactory.From(
            new RentalCompletedIntegrationEvent(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 150m, 0, "USD", Now));

        notification.ShouldNotBeNull();
        notification.Message.ShouldContain("on time");
    }

    [Fact]
    public void A_completed_event_with_delay_mentions_the_late_days()
    {
        var notification = NotificationFactory.From(
            new RentalCompletedIntegrationEvent(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 250m, 2, "USD", Now));

        notification.ShouldNotBeNull();
        notification.Message.ShouldContain("2 late day(s)");
    }

    [Fact]
    public void The_notification_id_is_the_event_id_so_a_reprocess_is_detectable()
    {
        var @event = new RentalConfirmedIntegrationEvent(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 150m, "USD", Now);

        // Dos traducciones del MISMO evento producen el mismo Id.
        NotificationFactory.From(@event).ShouldNotBeNull().Id.ShouldBe(@event.EventId);
        NotificationFactory.From(@event).ShouldNotBeNull().Id.ShouldBe(@event.EventId);
    }

    [Fact]
    public void Every_notification_keeps_the_event_type_it_came_from()
    {
        var notification = NotificationFactory.From(
            new RentalStartedIntegrationEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now, Now));

        notification.ShouldNotBeNull().EventType.ShouldBe(IntegrationEventTypes.RentalStarted);
    }
}

public sealed class InMemoryNotificationStoreTests
{
    private static Notification NotificationFor(Guid customerId) =>
        new(Guid.NewGuid(), Guid.NewGuid(), customerId, "rental.confirmed", "msg", DateTimeOffset.UtcNow);

    [Fact]
    public async Task Listing_without_a_filter_returns_everything()
    {
        var store = new InMemoryNotificationStore();
        await store.AddAsync(NotificationFor(Guid.NewGuid()));
        await store.AddAsync(NotificationFor(Guid.NewGuid()));

        (await store.ListAsync(null)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Listing_by_customer_filters_the_result()
    {
        var customerId = Guid.NewGuid();
        var store = new InMemoryNotificationStore();
        await store.AddAsync(NotificationFor(customerId));
        await store.AddAsync(NotificationFor(Guid.NewGuid()));

        (await store.ListAsync(customerId)).ShouldHaveSingleItem().CustomerId.ShouldBe(customerId);
    }

    [Fact]
    public async Task Adding_the_same_id_twice_keeps_only_the_first()
    {
        var store = new InMemoryNotificationStore();
        var notification = NotificationFor(Guid.NewGuid());

        (await store.AddAsync(notification)).ShouldBeTrue();
        (await store.AddAsync(notification with { Message = "otro texto" })).ShouldBeFalse();

        var stored = (await store.ListAsync(null)).ShouldHaveSingleItem();
        stored.Message.ShouldBe("msg");
    }

    [Fact]
    public async Task The_newest_notification_comes_first()
    {
        var customerId = Guid.NewGuid();
        var store = new InMemoryNotificationStore();
        var older = NotificationFor(customerId) with { CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10) };
        var newer = NotificationFor(customerId) with { CreatedAt = DateTimeOffset.UtcNow };

        await store.AddAsync(older);
        await store.AddAsync(newer);

        (await store.ListAsync(customerId))[0].Id.ShouldBe(newer.Id);
    }
}

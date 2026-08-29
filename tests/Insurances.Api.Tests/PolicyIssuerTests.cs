using Insurances.Api;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Contracts;

namespace Insurances.Api.Tests;

/// <summary>
/// La regla de negocio del servicio, aislada del transporte. Se usa el almacen
/// real porque las transiciones dependen del estado previo: con un doble se
/// estaria probando el doble.
/// </summary>
public sealed class PolicyIssuerTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);

    private readonly InMemoryPolicyStore _store = new();

    private PolicyIssuer CreateSut() => new(_store, NullLogger<PolicyIssuer>.Instance);

    private static readonly Guid RentalId = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");
    private static readonly Guid CustomerId = Guid.Parse("bbbbbbbb-1111-2222-3333-444444444444");
    private static readonly Guid VehicleId = Guid.Parse("cccccccc-1111-2222-3333-444444444444");

    private static RentalRequestedIntegrationEvent Requested(decimal total = 150m, int days = 3) =>
        new(RentalId, CustomerId, VehicleId, Now.AddDays(10), Now.AddDays(10 + days), total, "USD", Now);

    private static RentalConfirmedIntegrationEvent Confirmed() =>
        new(RentalId, CustomerId, VehicleId, 150m, "USD", Now);

    private async Task<Policy> CurrentPolicyAsync() =>
        (await _store.FindAsync(RentalId)).ShouldNotBeNull();

    [Fact]
    public async Task A_rental_request_drafts_a_policy()
    {
        var handled = await CreateSut().HandleAsync(Requested());

        handled.ShouldBeTrue();
        var policy = await CurrentPolicyAsync();
        policy.Status.ShouldBe(PolicyStatus.Draft);
        policy.Coverage.ShouldBe("standard");
        policy.CustomerId.ShouldBe(CustomerId);
        // standard: minimo 9/dia x 3 = 27; 12% de 150 = 18. Gana el minimo.
        policy.Premium.ShouldBe(27m);
    }

    [Fact]
    public async Task The_policy_number_is_derived_from_the_rental_so_it_is_stable()
    {
        await CreateSut().HandleAsync(Requested());

        (await CurrentPolicyAsync()).Number.ShouldBe(Policy.NumberFor(RentalId));
    }

    [Fact]
    public async Task Reprocessing_the_request_does_not_draft_a_second_policy()
    {
        var sut = CreateSut();
        await sut.HandleAsync(Requested());

        var handledAgain = await sut.HandleAsync(Requested());

        handledAgain.ShouldBeFalse();
        (await _store.ListAsync(rentalId: RentalId)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Confirming_the_rental_activates_the_policy()
    {
        var sut = CreateSut();
        await sut.HandleAsync(Requested());

        var handled = await sut.HandleAsync(Confirmed());

        handled.ShouldBeTrue();
        (await CurrentPolicyAsync()).Status.ShouldBe(PolicyStatus.Active);
    }

    [Fact]
    public async Task Reprocessing_the_confirmation_changes_nothing()
    {
        var sut = CreateSut();
        await sut.HandleAsync(Requested());
        await sut.HandleAsync(Confirmed());

        (await sut.HandleAsync(Confirmed())).ShouldBeFalse();
        (await CurrentPolicyAsync()).Status.ShouldBe(PolicyStatus.Active);
    }

    [Fact]
    public async Task Cancelling_the_rental_cancels_the_policy()
    {
        var sut = CreateSut();
        await sut.HandleAsync(Requested());
        await sut.HandleAsync(Confirmed());

        var handled = await sut.HandleAsync(
            new RentalCancelledIntegrationEvent(RentalId, CustomerId, VehicleId, 150m, 150m, 0m, 100m, "USD", Now));

        handled.ShouldBeTrue();
        (await CurrentPolicyAsync()).Status.ShouldBe(PolicyStatus.Cancelled);
    }

    [Fact]
    public async Task Completing_the_rental_expires_the_policy()
    {
        var sut = CreateSut();
        await sut.HandleAsync(Requested());
        await sut.HandleAsync(Confirmed());

        var handled = await sut.HandleAsync(
            new RentalCompletedIntegrationEvent(RentalId, CustomerId, VehicleId, 150m, 0, "USD", Now));

        handled.ShouldBeTrue();
        (await CurrentPolicyAsync()).Status.ShouldBe(PolicyStatus.Expired);
    }

    [Fact]
    public async Task A_cancelled_policy_cannot_be_expired_afterwards()
    {
        var sut = CreateSut();
        await sut.HandleAsync(Requested());
        await sut.HandleAsync(
            new RentalCancelledIntegrationEvent(RentalId, CustomerId, VehicleId, 150m, 0m, 150m, 0m, "USD", Now));

        var handled = await sut.HandleAsync(
            new RentalCompletedIntegrationEvent(RentalId, CustomerId, VehicleId, 150m, 0, "USD", Now));

        handled.ShouldBeFalse();
        (await CurrentPolicyAsync()).Status.ShouldBe(PolicyStatus.Cancelled);
    }

    [Fact]
    public async Task Extending_the_rental_moves_the_validity_and_recomputes_the_premium()
    {
        var sut = CreateSut();
        await sut.HandleAsync(Requested());
        await sut.HandleAsync(Confirmed());

        var newEnd = Now.AddDays(20);
        var handled = await sut.HandleAsync(
            new RentalExtendedIntegrationEvent(RentalId, CustomerId, VehicleId, newEnd, 500m, "USD", Now));

        handled.ShouldBeTrue();
        var policy = await CurrentPolicyAsync();
        policy.ValidTo.ShouldBe(newEnd);
        // 10 dias de vigencia: minimo 9 x 10 = 90; 12% de 500 = 60. Gana el minimo.
        policy.Premium.ShouldBe(90m);
    }

    [Fact]
    public async Task An_extension_of_a_cancelled_policy_is_ignored()
    {
        var sut = CreateSut();
        await sut.HandleAsync(Requested());
        await sut.HandleAsync(
            new RentalCancelledIntegrationEvent(RentalId, CustomerId, VehicleId, 150m, 0m, 150m, 0m, "USD", Now));

        var handled = await sut.HandleAsync(
            new RentalExtendedIntegrationEvent(RentalId, CustomerId, VehicleId, Now.AddDays(30), 500m, "USD", Now));

        handled.ShouldBeFalse();
    }

    [Fact]
    public async Task An_event_for_an_unknown_rental_is_ignored()
    {
        (await CreateSut().HandleAsync(Confirmed())).ShouldBeFalse();
    }

    [Fact]
    public async Task A_started_rental_does_not_affect_the_policy()
    {
        var sut = CreateSut();
        await sut.HandleAsync(Requested());
        await sut.HandleAsync(Confirmed());

        var handled = await sut.HandleAsync(
            new RentalStartedIntegrationEvent(RentalId, CustomerId, VehicleId, Now, Now));

        handled.ShouldBeFalse();
        (await CurrentPolicyAsync()).Status.ShouldBe(PolicyStatus.Active);
    }

    [Fact]
    public async Task A_null_event_is_rejected()
    {
        await Should.ThrowAsync<ArgumentNullException>(() => CreateSut().HandleAsync(null!));
    }
}

public sealed class InMemoryPolicyStoreTests
{
    private static Policy PolicyFor(Guid rentalId, Guid customerId) => new(
        Policy.NumberFor(rentalId),
        rentalId,
        customerId,
        "standard",
        27m,
        "USD",
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddDays(3),
        PolicyStatus.Draft,
        DateTimeOffset.UtcNow);

    [Fact]
    public async Task Saving_twice_for_the_same_rental_replaces_the_policy()
    {
        var rentalId = Guid.NewGuid();
        var store = new InMemoryPolicyStore();
        var policy = PolicyFor(rentalId, Guid.NewGuid());

        await store.SaveAsync(policy);
        await store.SaveAsync(policy with { Status = PolicyStatus.Active });

        (await store.ListAsync(rentalId: rentalId)).ShouldHaveSingleItem()
            .Status.ShouldBe(PolicyStatus.Active);
    }

    [Fact]
    public async Task Listing_filters_by_customer_and_by_rental()
    {
        var customerId = Guid.NewGuid();
        var rentalId = Guid.NewGuid();
        var store = new InMemoryPolicyStore();
        await store.SaveAsync(PolicyFor(rentalId, customerId));
        await store.SaveAsync(PolicyFor(Guid.NewGuid(), customerId));
        await store.SaveAsync(PolicyFor(Guid.NewGuid(), Guid.NewGuid()));

        (await store.ListAsync(customerId: customerId)).Count.ShouldBe(2);
        (await store.ListAsync(rentalId: rentalId)).ShouldHaveSingleItem();
        (await store.ListAsync()).Count.ShouldBe(3);
    }

    [Fact]
    public async Task Finding_an_unknown_rental_returns_null()
    {
        (await new InMemoryPolicyStore().FindAsync(Guid.NewGuid())).ShouldBeNull();
    }
}

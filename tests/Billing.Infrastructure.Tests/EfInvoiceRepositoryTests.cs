using Billing.Domain;
using Billing.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Billing.Infrastructure.Tests;

/// <summary>
/// PostgreSQL real con Testcontainers. Lo interesante de este servicio frente a
/// Rentals es <c>OwnsMany</c>: las lineas viven en su propia tabla y el mapeo
/// tiene que reconstruir la coleccion al cargar.
/// </summary>
public sealed class BillingPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("billing")
        .WithUsername("billing")
        .WithPassword("billing")
        .Build();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public BillingDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<BillingDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options);

    public async Task ResetAsync()
    {
        await using var context = CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE billing.invoice_lines, billing.invoices RESTART IDENTITY CASCADE;");
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class BillingPostgresCollection : ICollectionFixture<BillingPostgresFixture>
{
    public const string Name = "billing-postgres";
}

[Collection(BillingPostgresCollection.Name)]
public sealed class EfInvoiceRepositoryTests(BillingPostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static Invoice IssuedInvoice(Guid rentalId, Guid customerId, decimal amount = 200m)
    {
        var invoice = Invoice.DraftFor(rentalId, customerId, "USD", Now);
        invoice.AddLine("rental", Money.Of(amount, "USD"));
        invoice.AddLine("extras", Money.Of(50m, "USD"));
        invoice.Issue(Now);
        return invoice;
    }

    private async Task StoreAsync(Invoice invoice)
    {
        await using var context = fixture.CreateContext();
        await new EfInvoiceRepository(context).AddAsync(invoice);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task An_invoice_and_its_lines_survive_the_round_trip()
    {
        var rentalId = Guid.NewGuid();
        var invoice = IssuedInvoice(rentalId, Guid.NewGuid());
        await StoreAsync(invoice);

        await using var context = fixture.CreateContext();
        var reloaded = (await new EfInvoiceRepository(context).GetByIdAsync(invoice.Id)).ShouldNotBeNull();

        reloaded.Number.ShouldBe(invoice.Number);
        reloaded.Status.ShouldBe(InvoiceStatus.Issued);
        reloaded.Lines.Count.ShouldBe(2);
        reloaded.Lines.Select(l => l.Concept).ShouldBe(["rental", "extras"]);
        reloaded.Lines[0].Amount.ShouldBe(Money.Of(200m, "USD"));
    }

    [Fact]
    public async Task The_totals_are_recomputed_from_the_lines_and_not_stored()
    {
        var invoice = IssuedInvoice(Guid.NewGuid(), Guid.NewGuid());
        await StoreAsync(invoice);

        await using var context = fixture.CreateContext();
        var reloaded = (await new EfInvoiceRepository(context).GetByIdAsync(invoice.Id)).ShouldNotBeNull();

        // 200 + 50 = 250, con 19 % de impuesto.
        reloaded.Subtotal.Amount.ShouldBe(250m);
        reloaded.Tax.Amount.ShouldBe(47.50m);
        reloaded.Total.Amount.ShouldBe(297.50m);
    }

    [Fact]
    public async Task A_second_invoice_for_the_same_rental_is_rejected_by_the_unique_index()
    {
        var rentalId = Guid.NewGuid();
        await StoreAsync(IssuedInvoice(rentalId, Guid.NewGuid()));

        // La idempotencia de la capa de aplicacion es la primera barrera; el
        // indice unico es la ultima, y solo se puede comprobar contra PostgreSQL.
        await Should.ThrowAsync<DbUpdateException>(() => StoreAsync(IssuedInvoice(rentalId, Guid.NewGuid())));
    }

    [Fact]
    public async Task GetByRental_finds_the_invoice_of_a_rental()
    {
        var rentalId = Guid.NewGuid();
        await StoreAsync(IssuedInvoice(rentalId, Guid.NewGuid()));

        await using var context = fixture.CreateContext();
        var found = await new EfInvoiceRepository(context).GetByRentalAsync(rentalId);

        found.ShouldNotBeNull().RentalId.ShouldBe(rentalId);
    }

    [Fact]
    public async Task ListByCustomer_returns_only_that_customer()
    {
        var customerId = Guid.NewGuid();
        await StoreAsync(IssuedInvoice(Guid.NewGuid(), customerId));
        await StoreAsync(IssuedInvoice(Guid.NewGuid(), customerId));
        await StoreAsync(IssuedInvoice(Guid.NewGuid(), Guid.NewGuid()));

        await using var context = fixture.CreateContext();
        var invoices = await new EfInvoiceRepository(context).ListByCustomerAsync(customerId);

        invoices.Count.ShouldBe(2);
        invoices.ShouldAllBe(i => i.CustomerId == customerId);
    }

    [Fact]
    public async Task A_state_transition_is_persisted_with_its_timestamp()
    {
        var invoice = IssuedInvoice(Guid.NewGuid(), Guid.NewGuid());
        await StoreAsync(invoice);

        await using (var context = fixture.CreateContext())
        {
            var repository = new EfInvoiceRepository(context);
            var loaded = (await repository.GetByIdAsync(invoice.Id)).ShouldNotBeNull();
            loaded.Pay(loaded.Total, Now.AddDays(1));
            await context.SaveChangesAsync();
        }

        await using var readContext = fixture.CreateContext();
        var paid = (await new EfInvoiceRepository(readContext).GetByIdAsync(invoice.Id)).ShouldNotBeNull();

        paid.Status.ShouldBe(InvoiceStatus.Paid);
        paid.PaidAt.ShouldBe(Now.AddDays(1));
    }

    [Fact]
    public async Task Timestamps_come_back_as_utc()
    {
        var invoice = IssuedInvoice(Guid.NewGuid(), Guid.NewGuid());
        await StoreAsync(invoice);

        await using var context = fixture.CreateContext();
        var reloaded = (await new EfInvoiceRepository(context).GetByIdAsync(invoice.Id)).ShouldNotBeNull();

        reloaded.CreatedAt.Offset.ShouldBe(TimeSpan.Zero);
        reloaded.CreatedAt.ShouldBe(Now);
    }
}

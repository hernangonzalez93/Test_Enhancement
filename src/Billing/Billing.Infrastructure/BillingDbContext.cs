using Billing.Application;
using Billing.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Billing.Infrastructure;

public sealed class BillingDbContext(DbContextOptions<BillingDbContext> options) : DbContext(options)
{
    public const string Schema = "billing";

    public DbSet<Invoice> Invoices => Set<Invoice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new InvoiceConfiguration());
    }
}

/// <summary>
/// Mapeo del agregado. La novedad frente a Rentals es <c>OwnsMany</c>: las lineas
/// son una coleccion de value objects que EF guarda en su propia tabla, sin que
/// el dominio conozca esa tabla ni tenga clave ajena alguna.
/// </summary>
public sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("invoices");

        builder.HasKey(invoice => invoice.Id);

        builder.Property(invoice => invoice.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new InvoiceId(value))
            .ValueGeneratedNever();

        builder.Property(invoice => invoice.Number).HasColumnName("number").HasMaxLength(20).IsRequired();
        builder.Property(invoice => invoice.RentalId).HasColumnName("rental_id").IsRequired();
        builder.Property(invoice => invoice.CustomerId).HasColumnName("customer_id").IsRequired();
        builder.Property(invoice => invoice.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();

        builder.Property(invoice => invoice.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(invoice => invoice.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(invoice => invoice.IssuedAt).HasColumnName("issued_at");
        builder.Property(invoice => invoice.PaidAt).HasColumnName("paid_at");
        builder.Property(invoice => invoice.VoidedAt).HasColumnName("voided_at");

        builder.OwnsMany(invoice => invoice.Lines, line =>
        {
            line.ToTable("invoice_lines");
            line.WithOwner().HasForeignKey("invoice_id");
            line.Property<int>("id");
            line.HasKey("id");
            line.Property(l => l.Concept).HasColumnName("concept").HasMaxLength(80).IsRequired();
            line.OwnsOne(l => l.Amount, money =>
            {
                money.Property(m => m.Amount).HasColumnName("amount").HasPrecision(18, 2).IsRequired();
                money.Property(m => m.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
            });
            line.Navigation(l => l.Amount).IsRequired();
        });

        // Los totales se calculan en el dominio a partir de las lineas: no se
        // persisten para que no puedan quedar desincronizados.
        builder.Ignore(invoice => invoice.Subtotal);
        builder.Ignore(invoice => invoice.Tax);
        builder.Ignore(invoice => invoice.Total);

        builder.HasIndex(invoice => invoice.RentalId).IsUnique().HasDatabaseName("ux_invoices_rental_id");
        builder.HasIndex(invoice => invoice.CustomerId).HasDatabaseName("ix_invoices_customer_id");
    }
}

public sealed class EfInvoiceRepository(BillingDbContext context) : IInvoiceRepository
{
    public async Task<Invoice?> GetByIdAsync(InvoiceId id, CancellationToken cancellationToken = default) =>
        await context.Invoices.FirstOrDefaultAsync(invoice => invoice.Id == id, cancellationToken);

    public async Task<Invoice?> GetByRentalAsync(Guid rentalId, CancellationToken cancellationToken = default) =>
        await context.Invoices.FirstOrDefaultAsync(invoice => invoice.RentalId == rentalId, cancellationToken);

    public async Task<IReadOnlyList<Invoice>> ListByCustomerAsync(
        Guid customerId,
        CancellationToken cancellationToken = default) =>
        await context.Invoices
            .Where(invoice => invoice.CustomerId == customerId)
            .OrderByDescending(invoice => invoice.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(Invoice invoice, CancellationToken cancellationToken = default) =>
        await context.Invoices.AddAsync(invoice, cancellationToken);
}

public sealed class EfUnitOfWork(BillingDbContext context) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public static class DependencyInjection
{
    public static IServiceCollection AddBillingInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<BillingDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("BillingDatabase"),
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", BillingDbContext.Schema)));

        services.AddScoped<IInvoiceRepository, EfInvoiceRepository>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IInvoiceService, InvoiceService>();

        return services;
    }
}

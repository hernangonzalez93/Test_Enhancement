using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rentals.Domain.Model;

namespace Rentals.Infrastructure.Persistence;

public sealed class RentalsDbContext(DbContextOptions<RentalsDbContext> options) : DbContext(options)
{
    public const string Schema = "rentals";

    public DbSet<Rental> Rentals => Set<Rental>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new RentalConfiguration());
    }

    /// <summary>
    /// Renueva el sello de concurrencia en cada escritura. Si otro proceso
    /// guardo entre el read y el write, el UPDATE no encuentra la fila con el
    /// sello esperado y EF lanza DbUpdateConcurrencyException.
    /// </summary>
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<Rental>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Property<Guid>(RentalConfiguration.ConcurrencyStamp).CurrentValue = Guid.CreateVersion7();
            }
        }

        return base.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Mapeo del agregado. Los value objects se guardan como columnas propias
/// (owned types) para que la base de datos no imponga su forma al dominio.
/// </summary>
public sealed class RentalConfiguration : IEntityTypeConfiguration<Rental>
{
    /// <summary>Nombre de la propiedad sombra que actua como token de concurrencia.</summary>
    public const string ConcurrencyStamp = "ConcurrencyStamp";

    public void Configure(EntityTypeBuilder<Rental> builder)
    {
        builder.ToTable("rentals");

        builder.HasKey(rental => rental.Id);

        builder.Property(rental => rental.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new RentalId(value))
            .ValueGeneratedNever();

        builder.Property(rental => rental.CustomerId)
            .HasColumnName("customer_id")
            .HasConversion(id => id.Value, value => new CustomerId(value))
            .IsRequired();

        builder.Property(rental => rental.VehicleId)
            .HasColumnName("vehicle_id")
            .HasConversion(id => id.Value, value => new VehicleId(value))
            .IsRequired();

        builder.Property(rental => rental.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(rental => rental.RequestedAt).HasColumnName("requested_at").IsRequired();
        builder.Property(rental => rental.ConfirmedAt).HasColumnName("confirmed_at");
        builder.Property(rental => rental.StartedAt).HasColumnName("started_at");
        builder.Property(rental => rental.CompletedAt).HasColumnName("completed_at");
        builder.Property(rental => rental.CancelledAt).HasColumnName("cancelled_at");
        builder.Property(rental => rental.ReturnedAt).HasColumnName("returned_at");
        builder.Property(rental => rental.LateDays).HasColumnName("late_days").HasDefaultValue(0);

        builder.OwnsOne(rental => rental.Period, period =>
        {
            period.Property(p => p.Start).HasColumnName("period_start").IsRequired();
            period.Property(p => p.End).HasColumnName("period_end").IsRequired();
        });
        builder.Navigation(rental => rental.Period).IsRequired();

        builder.OwnsOne(rental => rental.License, license =>
        {
            license.Property(l => l.Number).HasColumnName("license_number").HasMaxLength(20).IsRequired();
            license.Property(l => l.ExpiresOn).HasColumnName("license_expires_on").IsRequired();
        });
        builder.Navigation(rental => rental.License).IsRequired();

        builder.OwnsOne(rental => rental.DailyRate, money =>
        {
            money.Property(m => m.Amount).HasColumnName("daily_rate_amount").HasPrecision(18, 2).IsRequired();
            money.Property(m => m.Currency).HasColumnName("daily_rate_currency").HasMaxLength(3).IsRequired();
        });
        builder.Navigation(rental => rental.DailyRate).IsRequired();

        builder.OwnsOne(rental => rental.EstimatedTotal, money =>
        {
            money.Property(m => m.Amount).HasColumnName("estimated_total_amount").HasPrecision(18, 2).IsRequired();
            money.Property(m => m.Currency).HasColumnName("estimated_total_currency").HasMaxLength(3).IsRequired();
        });
        builder.Navigation(rental => rental.EstimatedTotal).IsRequired();

        // FinalTotal y RefundAmount son opcionales: solo existen tras completar
        // o cancelar. Sus columnas quedan anulables.
        builder.OwnsOne(rental => rental.FinalTotal, money =>
        {
            money.Property(m => m.Amount).HasColumnName("final_total_amount").HasPrecision(18, 2);
            money.Property(m => m.Currency).HasColumnName("final_total_currency").HasMaxLength(3);
        });

        builder.OwnsOne(rental => rental.RefundAmount, money =>
        {
            money.Property(m => m.Amount).HasColumnName("refund_amount").HasPrecision(18, 2);
            money.Property(m => m.Currency).HasColumnName("refund_currency").HasMaxLength(3);
        });

        // Concurrencia optimista con una propiedad sombra: la columna existe en
        // la tabla pero NO en el agregado. El dominio no carga con un contador
        // de version, y aun asi dos escrituras simultaneas chocan.
        builder.Property<Guid>(ConcurrencyStamp)
            .HasColumnName("concurrency_stamp")
            .IsConcurrencyToken();

        // Los eventos de dominio viven solo en memoria: jamas se persisten aqui.
        builder.Ignore(rental => rental.DomainEvents);

        builder.HasIndex(rental => rental.CustomerId).HasDatabaseName("ix_rentals_customer_id");
        builder.HasIndex(rental => rental.VehicleId).HasDatabaseName("ix_rentals_vehicle_id");
        builder.HasIndex(rental => rental.Status).HasDatabaseName("ix_rentals_status");
    }
}

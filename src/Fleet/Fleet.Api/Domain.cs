using Microsoft.EntityFrameworkCore;

namespace Fleet.Api;

/// <summary>
/// Vehiculo del inventario. Fleet es el dueno de la disponibilidad: Rentals
/// nunca la escribe, solo la consulta y publica eventos que Fleet interpreta.
/// </summary>
public sealed class Vehicle
{
    public Guid Id { get; set; }

    public string Model { get; set; } = string.Empty;

    public string VehicleClass { get; set; } = string.Empty;

    public string LicensePlate { get; set; } = string.Empty;

    public decimal DailyRate { get; set; }

    public string Currency { get; set; } = "USD";

    public bool Available { get; set; } = true;
}

public sealed class FleetDbContext(DbContextOptions<FleetDbContext> options) : DbContext(options)
{
    public const string Schema = "fleet";

    public DbSet<Vehicle> Vehicles => Set<Vehicle>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        var vehicle = modelBuilder.Entity<Vehicle>();
        vehicle.ToTable("vehicles");
        vehicle.HasKey(v => v.Id);
        vehicle.Property(v => v.Id).HasColumnName("id").ValueGeneratedNever();
        vehicle.Property(v => v.Model).HasColumnName("model").HasMaxLength(120).IsRequired();
        vehicle.Property(v => v.VehicleClass).HasColumnName("vehicle_class").HasMaxLength(30).IsRequired();
        vehicle.Property(v => v.LicensePlate).HasColumnName("license_plate").HasMaxLength(15).IsRequired();
        vehicle.Property(v => v.DailyRate).HasColumnName("daily_rate").HasPrecision(18, 2);
        vehicle.Property(v => v.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
        vehicle.Property(v => v.Available).HasColumnName("available");
        vehicle.HasIndex(v => v.LicensePlate).IsUnique().HasDatabaseName("ux_vehicles_license_plate");
    }
}

public sealed record VehicleResponse(
    Guid Id,
    string Model,
    string VehicleClass,
    string LicensePlate,
    decimal DailyRate,
    string Currency,
    bool Available)
{
    public static VehicleResponse From(Vehicle vehicle) => new(
        vehicle.Id,
        vehicle.Model,
        vehicle.VehicleClass,
        vehicle.LicensePlate,
        vehicle.DailyRate,
        vehicle.Currency,
        vehicle.Available);
}

public sealed record CreateVehicleRequest(
    Guid? Id,
    string Model,
    string VehicleClass,
    string LicensePlate,
    decimal DailyRate,
    string? Currency);

using Microsoft.EntityFrameworkCore;

namespace Fleet.Api;

/// <summary>
/// Semilla determinista: los ids son fijos para que las pruebas de humo y E2E
/// puedan referenciar vehiculos concretos sin depender del orden de arranque.
/// </summary>
public static class FleetSeed
{
    public static readonly Guid EconomyVehicleId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid SuvVehicleId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid LuxuryVehicleId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid CompactVehicleId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly Guid SecondEconomyVehicleId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    public static readonly Guid SecondCompactVehicleId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    public static readonly Guid SecondSuvVehicleId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    public static readonly Guid SecondLuxuryVehicleId = Guid.Parse("88888888-8888-8888-8888-888888888888");

    // Ocho vehiculos y no cuatro: cada prueba E2E que confirma una renta deja
    // su vehiculo bloqueado, asi que la suite necesita uno propio por escenario.
    public static IReadOnlyList<Vehicle> Vehicles =>
    [
        new() { Id = EconomyVehicleId, Model = "Renault Kwid", VehicleClass = "economy", LicensePlate = "ECO-001", DailyRate = 30m, Currency = "USD", Available = true },
        new() { Id = SuvVehicleId, Model = "Toyota RAV4", VehicleClass = "suv", LicensePlate = "SUV-002", DailyRate = 60m, Currency = "USD", Available = true },
        new() { Id = LuxuryVehicleId, Model = "BMW Serie 5", VehicleClass = "luxury", LicensePlate = "LUX-003", DailyRate = 120m, Currency = "USD", Available = true },
        new() { Id = CompactVehicleId, Model = "Mazda 3", VehicleClass = "compact", LicensePlate = "CMP-004", DailyRate = 40m, Currency = "USD", Available = true },
        new() { Id = SecondEconomyVehicleId, Model = "Chevrolet Spark", VehicleClass = "economy", LicensePlate = "ECO-005", DailyRate = 28m, Currency = "USD", Available = true },
        new() { Id = SecondCompactVehicleId, Model = "Toyota Corolla", VehicleClass = "compact", LicensePlate = "CMP-006", DailyRate = 45m, Currency = "USD", Available = true },
        new() { Id = SecondSuvVehicleId, Model = "Honda CR-V", VehicleClass = "suv", LicensePlate = "SUV-007", DailyRate = 65m, Currency = "USD", Available = true },
        new() { Id = SecondLuxuryVehicleId, Model = "Mercedes Clase E", VehicleClass = "luxury", LicensePlate = "LUX-008", DailyRate = 130m, Currency = "USD", Available = true }
    ];

    public static async Task EnsureSeededAsync(FleetDbContext context, CancellationToken cancellationToken = default)
    {
        foreach (var vehicle in Vehicles)
        {
            var exists = await context.Vehicles.AnyAsync(v => v.Id == vehicle.Id, cancellationToken);
            if (!exists)
            {
                context.Vehicles.Add(vehicle);
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}

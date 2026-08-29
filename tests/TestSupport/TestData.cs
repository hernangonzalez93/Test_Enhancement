using Rentals.Application.Abstractions;

namespace TestSupport;

/// <summary>Datos canonicos reutilizados por varias suites.</summary>
public static class TestData
{
    public static readonly Guid EconomyVehicleId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid SuvVehicleId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid LuxuryVehicleId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid CompactVehicleId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    public const string ValidLicense = "LIC-12345";

    public static VehicleSnapshot AvailableVehicle(Guid? id = null) => new(
        id ?? EconomyVehicleId,
        "Renault Kwid",
        "economy",
        30m,
        "USD",
        Available: true);

    public static VehicleSnapshot UnavailableVehicle(Guid? id = null) =>
        AvailableVehicle(id) with { Available = false };

    public static PricingQuote QuoteOf(decimal dailyRate, int days, string currency = "USD") => new(
        dailyRate,
        dailyRate * days,
        currency,
        [new PricingLine("base", dailyRate)]);
}

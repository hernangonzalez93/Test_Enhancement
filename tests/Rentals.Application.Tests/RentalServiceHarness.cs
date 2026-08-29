using Microsoft.Extensions.Logging.Abstractions;
using Rentals.Application.Abstractions;
using Rentals.Application.Rentals;
using Rentals.Domain.Model;
using Shared.Contracts;
using TestSupport;

namespace Rentals.Application.Tests;

/// <summary>
/// Arnes de pruebas del caso de uso: crea el servicio con todos sus puertos
/// sustituidos por dobles y con un comportamiento por defecto valido.
/// Cada prueba solo reconfigura el puerto que le interesa, y asi el "arrange"
/// de cada test dice exactamente que hace distinta a esa prueba.
/// </summary>
public sealed class RentalServiceHarness
{
    public RentalServiceHarness()
    {
        Repository = Substitute.For<IRentalRepository>();
        UnitOfWork = Substitute.For<IUnitOfWork>();
        VehicleCatalog = Substitute.For<IVehicleCatalog>();
        PricingCalculator = Substitute.For<IPricingCalculator>();
        EventPublisher = Substitute.For<IEventPublisher>();
        Clock = new FixedClock();

        // Camino feliz por defecto.
        VehicleCatalog.FindAsync(Arg.Any<VehicleId>(), Arg.Any<CancellationToken>())
            .Returns(TestData.AvailableVehicle());

        Repository.HasOverlappingRentalAsync(
                Arg.Any<VehicleId>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(false);

        PricingCalculator.QuoteAsync(Arg.Any<PricingRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<PricingRequest>();
                return TestData.QuoteOf(request.BaseDailyRate, request.Days, request.Currency);
            });

        UnitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(1);

        Service = new RentalService(
            Repository,
            UnitOfWork,
            VehicleCatalog,
            PricingCalculator,
            EventPublisher,
            Clock,
            NullLogger<RentalService>.Instance);
    }

    public IRentalRepository Repository { get; }

    public IUnitOfWork UnitOfWork { get; }

    public IVehicleCatalog VehicleCatalog { get; }

    public IPricingCalculator PricingCalculator { get; }

    public IEventPublisher EventPublisher { get; }

    public FixedClock Clock { get; }

    public RentalService Service { get; }

    public static RequestRentalCommand ValidCommand(
        Guid? customerId = null,
        Guid? vehicleId = null,
        DateTimeOffset? start = null,
        DateTimeOffset? end = null,
        string license = TestData.ValidLicense,
        DateTimeOffset? licenseExpiry = null) => new(
        customerId ?? Guid.CreateVersion7(),
        vehicleId ?? TestData.EconomyVehicleId,
        start ?? FixedClock.DefaultNow.AddDays(10),
        end ?? FixedClock.DefaultNow.AddDays(13),
        license,
        licenseExpiry ?? FixedClock.DefaultNow.AddYears(3),
        []);

    /// <summary>Prepara el repositorio para devolver la renta indicada.</summary>
    public void GivenStoredRental(Rental rental) =>
        Repository.GetByIdAsync(rental.Id, Arg.Any<CancellationToken>()).Returns(rental);

    /// <summary>Captura los eventos que se enviaron al bus.</summary>
    public List<IIntegrationEvent> PublishedEvents()
    {
        var published = new List<IIntegrationEvent>();
        foreach (var call in EventPublisher.ReceivedCalls())
        {
            if (call.GetArguments()[0] is IReadOnlyCollection<IIntegrationEvent> batch)
            {
                published.AddRange(batch);
            }
        }

        return published;
    }
}

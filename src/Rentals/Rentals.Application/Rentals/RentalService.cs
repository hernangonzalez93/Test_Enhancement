using Microsoft.Extensions.Logging;
using Rentals.Application.Abstractions;
using Rentals.Application.Common;
using Rentals.Domain.Exceptions;
using Rentals.Domain.Model;

namespace Rentals.Application.Rentals;

/// <summary>
/// Orquestacion del caso de uso. Aqui NO hay reglas de negocio: solo se
/// coordinan puertos (catalogo, precios, repositorio, unidad de trabajo y bus)
/// y se traduce el resultado. Toda decision de negocio vive en el agregado.
/// </summary>
public sealed class RentalService(
    IRentalRepository repository,
    IUnitOfWork unitOfWork,
    IVehicleCatalog vehicleCatalog,
    IPricingCalculator pricingCalculator,
    IEventPublisher eventPublisher,
    IClock clock,
    ILogger<RentalService> logger) : IRentalService
{
    public async Task<Result<RentalDto>> RequestAsync(
        RequestRentalCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        RentalPeriod period;
        DriverLicense license;
        try
        {
            period = RentalPeriod.Create(command.PeriodStart, command.PeriodEnd);
            license = DriverLicense.Create(command.LicenseNumber, command.LicenseExpiresOn);
        }
        catch (DomainException exception)
        {
            return Result<RentalDto>.Failure(exception.Code, exception.Message);
        }

        var vehicleId = VehicleId.From(command.VehicleId);

        VehicleSnapshot? vehicle;
        try
        {
            vehicle = await vehicleCatalog.FindAsync(vehicleId, cancellationToken);
        }
        catch (ExternalServiceUnavailableException exception)
        {
            logger.LogError(exception, "Fleet service unavailable while requesting a rental.");
            return Result<RentalDto>.Failure(RentalErrors.FleetUnavailable);
        }

        if (vehicle is null)
        {
            return Result<RentalDto>.Failure(RentalErrors.VehicleNotFound(command.VehicleId));
        }

        if (!vehicle.Available)
        {
            return Result<RentalDto>.Failure(RentalErrors.VehicleUnavailable);
        }

        var overlaps = await repository.HasOverlappingRentalAsync(
            vehicleId,
            period.Start,
            period.End,
            cancellationToken);

        if (overlaps)
        {
            return Result<RentalDto>.Failure(RentalErrors.OverlappingRental);
        }

        PricingQuote quote;
        try
        {
            quote = await pricingCalculator.QuoteAsync(
                new PricingRequest(
                    vehicle.VehicleClass,
                    vehicle.BaseDailyRate,
                    period.TotalDays,
                    command.Extras ?? [],
                    vehicle.Currency),
                cancellationToken);
        }
        catch (ExternalServiceUnavailableException exception)
        {
            logger.LogError(exception, "Pricing service unavailable while requesting a rental.");
            return Result<RentalDto>.Failure(RentalErrors.PricingUnavailable);
        }

        Rental rental;
        try
        {
            rental = Rental.Request(
                RentalId.New(),
                CustomerId.From(command.CustomerId),
                vehicleId,
                period,
                license,
                Money.Of(quote.DailyRate, quote.Currency),
                clock.UtcNow);
        }
        catch (DomainException exception)
        {
            return Result<RentalDto>.Failure(exception.Code, exception.Message);
        }

        await repository.AddAsync(rental, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await PublishAsync(rental, cancellationToken);

        return Result<RentalDto>.Success(RentalDto.From(rental));
    }

    public Task<Result<RentalDto>> ConfirmAsync(Guid rentalId, CancellationToken cancellationToken = default) =>
        MutateAsync(rentalId, static (rental, now) => rental.Confirm(now), cancellationToken);

    public Task<Result<RentalDto>> CancelAsync(Guid rentalId, CancellationToken cancellationToken = default) =>
        MutateAsync(rentalId, static (rental, now) => rental.Cancel(now), cancellationToken);

    public Task<Result<RentalDto>> StartAsync(Guid rentalId, CancellationToken cancellationToken = default) =>
        MutateAsync(rentalId, static (rental, now) => rental.Start(now), cancellationToken);

    public Task<Result<RentalDto>> CompleteAsync(Guid rentalId, CancellationToken cancellationToken = default) =>
        MutateAsync(rentalId, static (rental, now) => rental.Complete(now), cancellationToken);

    public async Task<Result<RentalDto>> GetAsync(Guid rentalId, CancellationToken cancellationToken = default)
    {
        var rental = await repository.GetByIdAsync(RentalId.From(rentalId), cancellationToken);

        return rental is null
            ? Result<RentalDto>.Failure(RentalErrors.NotFound(rentalId))
            : Result<RentalDto>.Success(RentalDto.From(rental));
    }

    public async Task<Result<IReadOnlyList<RentalDto>>> ListByCustomerAsync(
        Guid customerId,
        CancellationToken cancellationToken = default)
    {
        var rentals = await repository.ListByCustomerAsync(CustomerId.From(customerId), cancellationToken);
        IReadOnlyList<RentalDto> dtos = rentals.Select(RentalDto.From).ToList();

        return Result<IReadOnlyList<RentalDto>>.Success(dtos);
    }

    /// <summary>
    /// Plantilla comun de las transiciones: cargar, aplicar la regla en el
    /// agregado, persistir y recien entonces publicar.
    /// </summary>
    private async Task<Result<RentalDto>> MutateAsync(
        Guid rentalId,
        Action<Rental, DateTimeOffset> transition,
        CancellationToken cancellationToken)
    {
        var rental = await repository.GetByIdAsync(RentalId.From(rentalId), cancellationToken);
        if (rental is null)
        {
            return Result<RentalDto>.Failure(RentalErrors.NotFound(rentalId));
        }

        try
        {
            transition(rental, clock.UtcNow);
        }
        catch (DomainException exception)
        {
            return Result<RentalDto>.Failure(exception.Code, exception.Message);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await PublishAsync(rental, cancellationToken);

        return Result<RentalDto>.Success(RentalDto.From(rental));
    }

    /// <summary>
    /// Publicacion despues del commit. Si el bus falla, la transaccion ya esta
    /// confirmada: se registra el error y se sigue, en vez de perder el cambio
    /// de negocio. Esta decision esta cubierta por una prueba explicita.
    /// </summary>
    private async Task PublishAsync(Rental rental, CancellationToken cancellationToken)
    {
        var integrationEvents = IntegrationEventMapper.Map(rental.DomainEvents);
        rental.ClearDomainEvents();

        if (integrationEvents.Count == 0)
        {
            return;
        }

        try
        {
            await eventPublisher.PublishAsync(integrationEvents, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to publish {Count} integration events for rental {RentalId}.",
                integrationEvents.Count,
                rental.Id);
        }
    }
}

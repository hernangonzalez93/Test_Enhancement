using Rentals.Application.Common;

namespace Rentals.Application.Rentals;

/// <summary>
/// Puerto de entrada (driving port). La API es solo un adaptador sobre esta
/// interfaz, y por eso las pruebas de API pueden sustituirla por un doble.
/// </summary>
public interface IRentalService
{
    Task<Result<RentalDto>> RequestAsync(RequestRentalCommand command, CancellationToken cancellationToken = default);

    Task<Result<RentalDto>> ConfirmAsync(Guid rentalId, CancellationToken cancellationToken = default);

    Task<Result<RentalDto>> CancelAsync(Guid rentalId, CancellationToken cancellationToken = default);

    /// <summary>Prorroga la renta hasta una nueva fecha de fin.</summary>
    Task<Result<RentalDto>> ExtendAsync(Guid rentalId, DateTimeOffset newEnd, CancellationToken cancellationToken = default);

    Task<Result<RentalDto>> StartAsync(Guid rentalId, CancellationToken cancellationToken = default);

    Task<Result<RentalDto>> CompleteAsync(Guid rentalId, CancellationToken cancellationToken = default);

    Task<Result<RentalDto>> GetAsync(Guid rentalId, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<RentalDto>>> ListByCustomerAsync(Guid customerId, CancellationToken cancellationToken = default);
}

using Rentals.Application.Common;
using Rentals.Application.Rentals;

namespace Rentals.Api.Endpoints;

/// <summary>Contrato HTTP de entrada. Deliberadamente distinto del comando de aplicacion.</summary>
public sealed record CreateRentalRequest(
    Guid CustomerId,
    Guid VehicleId,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    string LicenseNumber,
    DateTimeOffset LicenseExpiresOn,
    IReadOnlyList<string>? Extras);

/// <summary>
/// Adaptador de entrada (driving adapter). No contiene reglas: valida la forma
/// del mensaje, delega en el puerto de aplicacion y traduce el resultado a HTTP.
/// </summary>
public static class RentalEndpoints
{
    public static IEndpointRouteBuilder MapRentalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/rentals").WithTags("Rentals");

        group.MapPost("/", CreateAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapGet("/", ListAsync);
        group.MapPost("/{id:guid}/confirm", ConfirmAsync);
        group.MapPost("/{id:guid}/cancel", CancelAsync);
        group.MapPost("/{id:guid}/start", StartAsync);
        group.MapPost("/{id:guid}/complete", CompleteAsync);

        return app;
    }

    private static async Task<IResult> CreateAsync(
        CreateRentalRequest request,
        IRentalService rentalService,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return Results.ValidationProblem(validationErrors);
        }

        var result = await rentalService.RequestAsync(
            new RequestRentalCommand(
                request.CustomerId,
                request.VehicleId,
                request.PeriodStart,
                request.PeriodEnd,
                request.LicenseNumber,
                request.LicenseExpiresOn,
                request.Extras),
            cancellationToken);

        return result.IsSuccess
            ? Results.Created($"/api/rentals/{result.Value!.Id}", result.Value)
            : ErrorMapping.ToProblem(result.Error);
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        IRentalService rentalService,
        CancellationToken cancellationToken) =>
        ToHttp(await rentalService.GetAsync(id, cancellationToken));

    private static async Task<IResult> ListAsync(
        Guid customerId,
        IRentalService rentalService,
        CancellationToken cancellationToken)
    {
        if (customerId == Guid.Empty)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["customerId"] = ["customerId is required."]
            });
        }

        var result = await rentalService.ListByCustomerAsync(customerId, cancellationToken);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : ErrorMapping.ToProblem(result.Error);
    }

    private static async Task<IResult> ConfirmAsync(
        Guid id,
        IRentalService rentalService,
        CancellationToken cancellationToken) =>
        ToHttp(await rentalService.ConfirmAsync(id, cancellationToken));

    private static async Task<IResult> CancelAsync(
        Guid id,
        IRentalService rentalService,
        CancellationToken cancellationToken) =>
        ToHttp(await rentalService.CancelAsync(id, cancellationToken));

    private static async Task<IResult> StartAsync(
        Guid id,
        IRentalService rentalService,
        CancellationToken cancellationToken) =>
        ToHttp(await rentalService.StartAsync(id, cancellationToken));

    private static async Task<IResult> CompleteAsync(
        Guid id,
        IRentalService rentalService,
        CancellationToken cancellationToken) =>
        ToHttp(await rentalService.CompleteAsync(id, cancellationToken));

    private static IResult ToHttp(Result<RentalDto> result) =>
        result.IsSuccess
            ? Results.Ok(result.Value)
            : ErrorMapping.ToProblem(result.Error);

    /// <summary>
    /// Validacion de forma, no de negocio: campos obligatorios y coherencia
    /// basica. Las reglas de negocio (periodo maximo, licencia vencida) siguen
    /// siendo responsabilidad del dominio.
    /// </summary>
    private static Dictionary<string, string[]> Validate(CreateRentalRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (request.CustomerId == Guid.Empty)
        {
            errors[nameof(request.CustomerId)] = ["customerId is required."];
        }

        if (request.VehicleId == Guid.Empty)
        {
            errors[nameof(request.VehicleId)] = ["vehicleId is required."];
        }

        if (string.IsNullOrWhiteSpace(request.LicenseNumber))
        {
            errors[nameof(request.LicenseNumber)] = ["licenseNumber is required."];
        }

        if (request.PeriodEnd <= request.PeriodStart)
        {
            errors[nameof(request.PeriodEnd)] = ["periodEnd must be after periodStart."];
        }

        return errors;
    }
}

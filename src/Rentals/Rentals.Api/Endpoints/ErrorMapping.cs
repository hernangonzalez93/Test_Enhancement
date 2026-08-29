using Microsoft.AspNetCore.Http.HttpResults;
using Rentals.Application.Common;

namespace Rentals.Api.Endpoints;

/// <summary>
/// Unico punto donde un codigo de error de negocio se convierte en un codigo
/// HTTP. Centralizarlo evita que cada endpoint invente su propia semantica y
/// hace que exista una sola prueba por regla de traduccion.
/// </summary>
public static class ErrorMapping
{
    public static int ToStatusCode(string errorCode) => errorCode switch
    {
        "rental.not_found" or "vehicle.not_found" => StatusCodes.Status404NotFound,
        "vehicle.unavailable" or "rental.overlapping" or "rental.invalid_state" or "rental.not_startable_yet"
            => StatusCodes.Status409Conflict,
        "pricing.unavailable" or "fleet.unavailable" => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status400BadRequest
    };

    public static ProblemHttpResult ToProblem(Error error) => TypedResults.Problem(
        detail: error.Message,
        statusCode: ToStatusCode(error.Code),
        title: error.Code,
        extensions: new Dictionary<string, object?> { ["errorCode"] = error.Code });
}

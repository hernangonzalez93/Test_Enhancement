using Billing.Application;
using Microsoft.AspNetCore.Mvc;

namespace Billing.Api.Controllers;

/// <summary>
/// Adaptador de entrada con **controllers clasicos**, a diferencia de Rentals,
/// que usa Minimal API. La responsabilidad es idéntica —validar la forma,
/// delegar en el puerto de aplicacion y traducir a HTTP— y el contraste sirve
/// para comparar los dos estilos sobre la misma arquitectura.
///
/// Diferencias practicas frente a Minimal API:
///   - `[ApiController]` activa la validacion automatica del modelo y el
///     ProblemDetails de 400 sin escribir codigo.
///   - El enrutado es declarativo por atributos.
///   - `ControllerBase` ofrece helpers tipados (Ok, NotFound, CreatedAtAction).
/// </summary>
[ApiController]
[Route("api/invoices")]
[Produces("application/json")]
public sealed class InvoicesController(IInvoiceService invoiceService) : ControllerBase
{
    /// <summary>Factura por su identificador.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<InvoiceDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken) =>
        ToHttp(await invoiceService.GetAsync(id, cancellationToken));

    /// <summary>Factura asociada a una renta, o las facturas de un cliente.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] Guid? rentalId,
        [FromQuery] Guid? customerId,
        CancellationToken cancellationToken)
    {
        if (rentalId is not null)
        {
            var byRental = await invoiceService.GetByRentalAsync(rentalId.Value, cancellationToken);

            // Consultar por renta devuelve una lista para que el cliente no
            // tenga que distinguir entre "no hay factura" y un 404.
            return Ok(byRental.IsSuccess ? new[] { byRental.Value! } : []);
        }

        if (customerId is null)
        {
            ModelState.AddModelError("customerId", "Either customerId or rentalId is required.");
            return ValidationProblem(ModelState);
        }

        var result = await invoiceService.ListByCustomerAsync(customerId.Value, cancellationToken);

        return Ok(result.Value);
    }

    /// <summary>Paga la factura por su importe total.</summary>
    [HttpPost("{id:guid}/pay")]
    [ProducesResponseType<InvoiceDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Pay(Guid id, CancellationToken cancellationToken) =>
        ToHttp(await invoiceService.PayAsync(id, cancellationToken));

    /// <summary>Anula la factura. Una factura pagada ya no se puede anular.</summary>
    [HttpPost("{id:guid}/void")]
    [ProducesResponseType<InvoiceDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Void(Guid id, CancellationToken cancellationToken) =>
        ToHttp(await invoiceService.VoidAsync(id, cancellationToken));

    private IActionResult ToHttp(Result<InvoiceDto> result) =>
        result.IsSuccess ? Ok(result.Value) : Problem(result.Error);

    /// <summary>
    /// Unico punto de traduccion de error de negocio a codigo HTTP, igual que
    /// ErrorMapping en Rentals.
    /// </summary>
    private ObjectResult Problem(Error error)
    {
        var status = error.Code switch
        {
            "invoice.not_found" => StatusCodes.Status404NotFound,
            "invoice.invalid_state" or "invoice.payment_mismatch" => StatusCodes.Status409Conflict,
            "invoice.nothing_to_bill" => StatusCodes.Status422UnprocessableEntity,
            _ => StatusCodes.Status400BadRequest
        };

        var problem = new ProblemDetails
        {
            Title = error.Code,
            Detail = error.Message,
            Status = status
        };
        problem.Extensions["errorCode"] = error.Code;

        return StatusCode(status, problem);
    }
}

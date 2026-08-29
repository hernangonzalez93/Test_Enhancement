namespace Rentals.Api.Infrastructure;

/// <summary>
/// Propaga (o crea) un identificador de correlacion. Sirve para seguir una
/// misma peticion desde el navegador hasta el mensaje de Kafka, y es lo que
/// permite que una prueba E2E encuentre el evento que genero su clic.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.ToString()
            : Guid.CreateVersion7().ToString();

        context.Items[HeaderName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        await next(context);
    }
}

using Shared.Contracts;

namespace Rentals.Api.Infrastructure;

/// <summary>
/// Propaga (o crea) un identificador de correlacion, y lo deja en tres sitios:
/// la respuesta, el <see cref="Activity"/> en curso —de donde lo recoge el
/// publicador de Kafka— y un ambito de log, para que TODA linea escrita durante
/// la peticion lo lleve sin que nadie tenga que acordarse de anadirlo.
///
/// Es lo que permite seguir una misma operacion desde el navegador hasta el
/// consumidor que reacciona al evento, tres servicios mas alla.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.ToString()
            : Guid.CreateVersion7().ToString();

        context.Items[HeaderName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        CorrelationContext.Set(correlationId);

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }
}

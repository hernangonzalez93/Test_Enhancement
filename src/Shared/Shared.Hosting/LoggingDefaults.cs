using Microsoft.Extensions.Logging;

namespace Shared.Hosting;

/// <summary>
/// Logging estructurado para los entornos donde alguien va a tener que buscar
/// algo entre miles de renglones.
/// </summary>
public static class LoggingDefaults
{
    /// <summary>
    /// En desarrollo deja la consola de texto, que es la que se lee comoda en
    /// una terminal. En cualquier otro entorno la sustituye por JSON.
    ///
    /// El codigo de este repositorio ya escribe con plantillas y parametros
    /// nombrados —<c>"Subscribed to {Topic} as {GroupId}"</c>—, asi que los
    /// campos ya existen; lo unico que hace este cambio es dejar de aplanarlos
    /// a texto. Con JSON, <c>Topic</c> y <c>GroupId</c> pasan a ser campos
    /// consultables en cualquier agregador de logs.
    ///
    /// <c>IncludeScopes</c> es imprescindible: sin el, el identificador de
    /// correlacion que abre cada peticion no aparece en las lineas de log.
    /// </summary>
    public static ILoggingBuilder AddStructuredConsole(this ILoggingBuilder logging, string environmentName)
    {
        if (environmentName is "Development" or "Testing")
        {
            return logging;
        }

        logging.ClearProviders();
        logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;
            options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        });

        return logging;
    }
}

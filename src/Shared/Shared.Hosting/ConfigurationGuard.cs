using Microsoft.Extensions.Configuration;

namespace Shared.Hosting;

/// <summary>
/// Se lanza cuando un servicio arranca fuera de desarrollo con configuracion
/// que solo tiene sentido en la maquina de alguien.
/// </summary>
public sealed class InsecureConfigurationException(string message) : Exception(message);

/// <summary>
/// Comprueba, al arrancar, que la configuracion de un entorno real no sea la
/// de desarrollo.
///
/// Los <c>appsettings.json</c> de este repositorio llevan cadenas de conexion
/// apuntando a localhost con contraseñas triviales. Eso es correcto: son las
/// credenciales del PostgreSQL del compose. El problema seria que un despliegue
/// mal configurado las heredase en silencio —porque nadie definio la variable
/// de entorno que debia sobrescribirlas— y el servicio arrancase igualmente,
/// fallando solo en la primera peticion que tocara la base de datos.
///
/// Fallar aqui convierte ese error en un contenedor que no llega a declararse
/// sano, que es cuando todavia se puede revertir sin que nadie lo note.
/// </summary>
public static class ConfigurationGuard
{
    private static readonly string[] SenalesDeDesarrollo =
    [
        "host=localhost",
        "host=127.0.0.1",
        "server=localhost"
    ];

    /// <summary>
    /// Revisa todas las cadenas bajo <c>ConnectionStrings</c>. No hace nada si
    /// el entorno es de desarrollo o de pruebas.
    /// </summary>
    public static void EnsureNoDevelopmentCredentials(IConfiguration configuration, string environmentName)
    {
        if (environmentName is "Development" or "Testing")
        {
            return;
        }

        foreach (var entrada in configuration.GetSection("ConnectionStrings").GetChildren())
        {
            var valor = entrada.Value;
            if (string.IsNullOrWhiteSpace(valor))
            {
                continue;
            }

            var normalizada = valor.Replace(" ", string.Empty).ToLowerInvariant();

            foreach (var senal in SenalesDeDesarrollo)
            {
                if (normalizada.Contains(senal))
                {
                    throw new InsecureConfigurationException(
                        $"La cadena de conexion '{entrada.Key}' apunta a la maquina local en el entorno " +
                        $"'{environmentName}'. Casi con seguridad falta la variable de entorno " +
                        $"ConnectionStrings__{entrada.Key}. El servicio no arranca a proposito.");
                }
            }
        }
    }
}

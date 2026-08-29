using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rentals.Application.Rentals;

namespace Rentals.Api.Tests;

/// <summary>
/// Arranca la API completa EN MEMORIA: enrutado, serializacion, middlewares,
/// validacion y mapeo de errores son reales. Lo unico sustituido es el puerto
/// de aplicacion, de modo que estas pruebas responden a una sola pregunta:
/// "dado un resultado del caso de uso, que devuelve HTTP?".
/// Sin base de datos ni broker, corren en milisegundos.
/// </summary>
public sealed class RentalsApiFactory : WebApplicationFactory<RentalsApiMarker>
{
    public IRentalService RentalService { get; } = Substitute.For<IRentalService>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Se reemplaza el registro real por el doble. Este es el punto
            // exacto donde la arquitectura hexagonal se paga sola.
            services.RemoveAll<IRentalService>();
            services.AddScoped(_ => RentalService);
        });
    }
}

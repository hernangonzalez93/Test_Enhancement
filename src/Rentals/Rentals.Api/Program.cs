using Microsoft.EntityFrameworkCore;
using Rentals.Api.Endpoints;
using Rentals.Api.Infrastructure;
using Rentals.Application;
using Rentals.Infrastructure;
using Rentals.Infrastructure.Persistence;
using Shared.Hosting;

var builder = WebApplication.CreateBuilder(args);

// Logging estructurado fuera de desarrollo, y un cortafuegos que impide
// arrancar en un entorno real con la configuracion de la maquina de alguien.
builder.Logging.AddStructuredConsole(builder.Environment.EnvironmentName);
ConfigurationGuard.EnsureNoDevelopmentCredentials(builder.Configuration, builder.Environment.EnvironmentName);


builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddRentalsApplication();
builder.Services.AddRentalsInfrastructure(builder.Configuration);

builder.Services.AddHealthChecks()
    .AddNpgSql(
        builder.Configuration.GetConnectionString("RentalsDatabase") ?? string.Empty,
        name: "postgres",
        tags: ["ready"]);

const string FrontendCorsPolicy = "frontend";
builder.Services.AddCors(options => options.AddPolicy(
    FrontendCorsPolicy,
    policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseCors(FrontendCorsPolicy);
app.UseMiddleware<CorrelationIdMiddleware>();

app.MapOpenApi();
app.MapRentalEndpoints();

// /health responde en cuanto el proceso vive; /health/ready espera a que
// PostgreSQL este accesible. La distincion es lo que hace utiles las pruebas
// de humo: separan "arranco" de "puede trabajar".
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready");

// Solo se migra cuando se pide explicitamente (contenedores). Las pruebas de
// API con WebApplicationFactory arrancan sin base de datos y no deben tocarla.
// Modo migracion: la MISMA imagen, invocada con `migrate`, aplica el esquema y
// termina sin servir trafico. En un despliegue con varias tareas, migrar al
// arrancar seria una carrera entre instancias; ademas obligaria a que el
// usuario de la aplicacion tuviese permisos de DDL de forma permanente.
// Con esto, migrar es un paso propio del pipeline, anterior y observable.
if (args.Contains("migrate"))
{
    using var migrationScope = app.Services.CreateScope();
    await migrationScope.ServiceProvider.GetRequiredService<RentalsDbContext>().Database.MigrateAsync();
    return;
}

if (app.Configuration.GetValue<bool>("Database:AutoMigrate"))
{
    using var scope = app.Services.CreateScope();
    var context = scope.ServiceProvider.GetRequiredService<RentalsDbContext>();
    await context.Database.MigrateAsync();
}

await app.RunAsync();

/// <summary>
/// Necesario para que WebApplicationFactory pueda referenciar el punto de
/// entrada de esta API desde el proyecto de pruebas.
/// </summary>
public partial class Program;

using Microsoft.EntityFrameworkCore;
using Rentals.Api.Endpoints;
using Rentals.Api.Infrastructure;
using Rentals.Application;
using Rentals.Infrastructure;
using Rentals.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

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

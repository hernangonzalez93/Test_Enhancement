using Billing.Api;
using Billing.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Shared.Hosting;

var builder = WebApplication.CreateBuilder(args);

// Logging estructurado fuera de desarrollo, y un cortafuegos que impide
// arrancar en un entorno real con la configuracion de la maquina de alguien.
builder.Logging.AddStructuredConsole(builder.Environment.EnvironmentName);
ConfigurationGuard.EnsureNoDevelopmentCredentials(builder.Configuration, builder.Environment.EnvironmentName);


// Controllers clasicos, a diferencia de la Minimal API de Rentals.
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

builder.Services.AddBillingInfrastructure(builder.Configuration);

builder.Services.Configure<KafkaConsumerOptions>(builder.Configuration.GetSection(KafkaConsumerOptions.SectionName));
builder.Services.AddSingleton<ConsumerReadiness>();
builder.Services.AddHostedService<RentalEventsConsumer>();

builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("BillingDatabase") ?? string.Empty, name: "postgres", tags: ["ready"])
    .AddCheck<KafkaConsumerHealthCheck>("kafka-consumer", tags: ["ready"]);

builder.Services.AddCors(options => options.AddPolicy(
    "frontend",
    policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseCors("frontend");
app.MapOpenApi();
app.MapControllers();

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "billing" }));
app.MapHealthChecks("/health/ready");

// Modo migracion: la MISMA imagen, invocada con `migrate`, aplica el esquema y
// termina sin servir trafico. En un despliegue con varias tareas, migrar al
// arrancar seria una carrera entre instancias; ademas obligaria a que el
// usuario de la aplicacion tuviese permisos de DDL de forma permanente.
// Con esto, migrar es un paso propio del pipeline, anterior y observable.
if (args.Contains("migrate"))
{
    using var migrationScope = app.Services.CreateScope();
    await migrationScope.ServiceProvider.GetRequiredService<BillingDbContext>().Database.MigrateAsync();
    return;
}

if (app.Configuration.GetValue<bool>("Database:AutoMigrate"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<BillingDbContext>().Database.MigrateAsync();
}

await app.RunAsync();

public partial class Program;

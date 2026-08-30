using Insurances.Api;
using Shared.Hosting;

var builder = WebApplication.CreateBuilder(args);

// Logging estructurado fuera de desarrollo, y un cortafuegos que impide
// arrancar en un entorno real con la configuracion de la maquina de alguien.
builder.Logging.AddStructuredConsole(builder.Environment.EnvironmentName);
ConfigurationGuard.EnsureNoDevelopmentCredentials(builder.Configuration, builder.Environment.EnvironmentName);


builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

// PolicyStatus es un enum: sin este convertidor viajaria como numero (0, 1, 2)
// y tanto el frontal como las pruebas verian "0" en lugar de "Draft".
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.Configure<KafkaConsumerOptions>(builder.Configuration.GetSection(KafkaConsumerOptions.SectionName));
builder.Services.AddSingleton<IPolicyStore, InMemoryPolicyStore>();
builder.Services.AddSingleton<IPolicyIssuer, PolicyIssuer>();
builder.Services.AddSingleton<ConsumerReadiness>();
builder.Services.AddHealthChecks().AddCheck<KafkaConsumerHealthCheck>("kafka-consumer", tags: ["ready"]);
builder.Services.AddHostedService<RentalEventsConsumer>();

builder.Services.AddCors(options => options.AddPolicy(
    "frontend",
    policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseCors("frontend");
app.MapOpenApi();

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "insurances" }));
app.MapHealthChecks("/health/ready");

// Cotizacion sin estado, igual que Pricing: util antes de rentar.
app.MapPost("/api/insurance/quotes", (PremiumRequest request) =>
{
    try
    {
        return Results.Ok(PremiumEngine.Quote(request));
    }
    catch (InsuranceException exception)
    {
        return Results.Problem(
            detail: exception.Message,
            statusCode: StatusCodes.Status400BadRequest,
            title: "insurance.invalid_request");
    }
});

app.MapGet("/api/insurance/coverages", () => Results.Ok(PremiumEngine.Coverages));

// Polizas emitidas a partir de los eventos de renta.
app.MapGet("/api/policies", async (Guid? customerId, Guid? rentalId, IPolicyStore store, CancellationToken ct) =>
    Results.Ok(await store.ListAsync(customerId, rentalId, ct)));

await app.RunAsync();

public partial class Program;

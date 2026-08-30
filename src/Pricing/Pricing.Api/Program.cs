using Pricing.Api;
using Shared.Hosting;

var builder = WebApplication.CreateBuilder(args);

// Logging estructurado fuera de desarrollo, y un cortafuegos que impide
// arrancar en un entorno real con la configuracion de la maquina de alguien.
builder.Logging.AddStructuredConsole(builder.Environment.EnvironmentName);
ConfigurationGuard.EnsureNoDevelopmentCredentials(builder.Configuration, builder.Environment.EnvironmentName);


builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.MapOpenApi();

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "pricing" }));

app.MapPost("/api/quotes", (QuoteRequest request) =>
{
    try
    {
        return Results.Ok(PricingEngine.Quote(request));
    }
    catch (PricingException exception)
    {
        return Results.Problem(
            detail: exception.Message,
            statusCode: StatusCodes.Status400BadRequest,
            title: "pricing.invalid_request");
    }
});

app.MapGet("/api/pricing/catalog", () => Results.Ok(new
{
    classes = PricingEngine.ClassMultipliers,
    extras = PricingEngine.ExtraDailyPrices
}));

await app.RunAsync();

public partial class Program;

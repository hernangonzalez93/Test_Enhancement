using Pricing.Api;

var builder = WebApplication.CreateBuilder(args);

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

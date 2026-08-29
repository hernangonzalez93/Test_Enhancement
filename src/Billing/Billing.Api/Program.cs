using Billing.Api;
using Billing.Infrastructure;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

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

if (app.Configuration.GetValue<bool>("Database:AutoMigrate"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<BillingDbContext>().Database.MigrateAsync();
}

await app.RunAsync();

public partial class Program;

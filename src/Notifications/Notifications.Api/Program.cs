using Notifications.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

builder.Services.Configure<KafkaConsumerOptions>(builder.Configuration.GetSection(KafkaConsumerOptions.SectionName));
builder.Services.AddSingleton<INotificationStore, InMemoryNotificationStore>();
builder.Services.AddSingleton<INotificationIngestor, NotificationIngestor>();
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

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "notifications" }));
app.MapHealthChecks("/health/ready");

app.MapGet("/api/notifications", async (Guid? customerId, INotificationStore store, CancellationToken ct) =>
    Results.Ok(await store.ListAsync(customerId, ct)));

await app.RunAsync();

public partial class Program;

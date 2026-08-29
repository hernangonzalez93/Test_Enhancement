using Fleet.Api;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

builder.Services.AddDbContext<FleetDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("FleetDatabase"),
        npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", FleetDbContext.Schema)));

builder.Services.Configure<KafkaConsumerOptions>(builder.Configuration.GetSection(KafkaConsumerOptions.SectionName));
builder.Services.AddScoped<IVehicleAvailabilityHandler, VehicleAvailabilityHandler>();
builder.Services.AddHostedService<RentalEventsConsumer>();

builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("FleetDatabase") ?? string.Empty, name: "postgres", tags: ["ready"]);

builder.Services.AddCors(options => options.AddPolicy(
    "frontend",
    policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseCors("frontend");
app.MapOpenApi();

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "fleet" }));
app.MapHealthChecks("/health/ready");

app.MapGet("/api/vehicles", async (FleetDbContext db, bool? availableOnly, CancellationToken ct) =>
{
    var query = db.Vehicles.AsNoTracking().AsQueryable();
    if (availableOnly == true)
    {
        query = query.Where(v => v.Available);
    }

    var vehicles = await query.OrderBy(v => v.Model).ToListAsync(ct);
    return Results.Ok(vehicles.Select(VehicleResponse.From));
});

app.MapGet("/api/vehicles/{id:guid}", async (Guid id, FleetDbContext db, CancellationToken ct) =>
{
    var vehicle = await db.Vehicles.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct);
    return vehicle is null
        ? Results.Problem(detail: $"Vehicle {id} was not found.", statusCode: StatusCodes.Status404NotFound, title: "vehicle.not_found")
        : Results.Ok(VehicleResponse.From(vehicle));
});

app.MapPost("/api/vehicles", async (CreateVehicleRequest request, FleetDbContext db, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Model) || string.IsNullOrWhiteSpace(request.LicensePlate))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["model"] = ["model and licensePlate are required."]
        });
    }

    var vehicle = new Vehicle
    {
        Id = request.Id ?? Guid.CreateVersion7(),
        Model = request.Model,
        VehicleClass = request.VehicleClass,
        LicensePlate = request.LicensePlate.ToUpperInvariant(),
        DailyRate = request.DailyRate,
        Currency = (request.Currency ?? "USD").ToUpperInvariant(),
        Available = true
    };

    db.Vehicles.Add(vehicle);
    await db.SaveChangesAsync(ct);

    return Results.Created($"/api/vehicles/{vehicle.Id}", VehicleResponse.From(vehicle));
});

if (app.Configuration.GetValue<bool>("Database:AutoMigrate"))
{
    using var scope = app.Services.CreateScope();
    var context = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
    await context.Database.MigrateAsync();
    await FleetSeed.EnsureSeededAsync(context);
}

await app.RunAsync();

public partial class Program;

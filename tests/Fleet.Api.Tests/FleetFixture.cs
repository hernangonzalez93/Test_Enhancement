using Fleet.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Fleet.Api.Tests;

/// <summary>
/// Fleet se prueba de punta a punta dentro del proceso: API real + PostgreSQL
/// real, con el consumidor de Kafka apagado. Asi cada prueba ejercita el mismo
/// camino que en produccion salvo el transporte de eventos, que tiene su propia
/// prueba de integracion.
/// </summary>
public sealed class FleetFixture : WebApplicationFactory<FleetApiMarker>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("fleet")
        .WithUsername("fleet")
        .WithPassword("fleet")
        .Build();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
        await context.Database.MigrateAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:FleetDatabase", _container.GetConnectionString());
        builder.UseSetting("Kafka:Enabled", "false");
        builder.UseSetting("Database:AutoMigrate", "false");
    }

    public FleetDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<FleetDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options);

    /// <summary>Deja la tabla con la semilla canonica antes de cada prueba.</summary>
    public async Task ResetAsync()
    {
        await using var context = CreateContext();
        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE fleet.vehicles;");
        await FleetSeed.EnsureSeededAsync(context);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class FleetCollection : ICollectionFixture<FleetFixture>
{
    public const string Name = "fleet";
}

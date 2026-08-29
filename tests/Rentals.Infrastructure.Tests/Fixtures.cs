using Microsoft.EntityFrameworkCore;
using Rentals.Infrastructure.Persistence;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;

namespace Rentals.Infrastructure.Tests;

/// <summary>
/// Levanta un PostgreSQL real en Docker y le aplica las migraciones.
/// Es la diferencia clave frente a un proveedor en memoria: aqui se prueban
/// el SQL generado, los tipos de columna, los indices y la concurrencia.
/// El contenedor se comparte por coleccion para pagar el arranque una sola vez.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("rentals")
        .WithUsername("rentals")
        .WithPassword("rentals")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public RentalsDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<RentalsDbContext>()
            .UseNpgsql(ConnectionString)
            .Options);

    /// <summary>
    /// Aislamiento entre pruebas: cada test empieza con la tabla vacia.
    /// Es mas rapido y mas predecible que rehacer el contenedor.
    /// </summary>
    public async Task ResetAsync()
    {
        await using var context = CreateContext();
        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE rentals.rentals;");
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

/// <summary>
/// Broker Kafka real en Docker (modo KRaft, sin ZooKeeper).
/// Solo asi se puede comprobar que la clave de particion, las cabeceras y el
/// JSON llegan tal como los espera el consumidor.
/// </summary>
public sealed class KafkaFixture : IAsyncLifetime
{
    private readonly KafkaContainer _container = new KafkaBuilder("confluentinc/cp-kafka:7.7.1").Build();

    public string BootstrapServers => _container.GetBootstrapAddress();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class KafkaCollection : ICollectionFixture<KafkaFixture>
{
    public const string Name = "kafka";
}

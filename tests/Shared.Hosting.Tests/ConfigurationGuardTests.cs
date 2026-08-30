using Microsoft.Extensions.Configuration;
using Shared.Hosting;

namespace Shared.Hosting.Tests;

/// <summary>
/// El cortafuegos que impide desplegar con la configuracion de la maquina de
/// alguien. Es una pieza pequeña, pero es de las pocas cuyo fallo no se nota
/// hasta que ya hay un problema en produccion: merece pruebas explicitas de
/// cada frontera.
/// </summary>
public sealed class ConfigurationGuardTests
{
    private static IConfiguration ConfigurationWith(params (string Key, string Value)[] connectionStrings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(connectionStrings.Select(c =>
                new KeyValuePair<string, string?>($"ConnectionStrings:{c.Key}", c.Value)))
            .Build();

    [Theory]
    [InlineData("Host=localhost;Port=5432;Database=rentals;Username=rentals;Password=rentals")]
    [InlineData("Host=127.0.0.1;Port=5432;Database=rentals")]
    [InlineData("Server=localhost;Database=rentals")]
    [InlineData("Host = localhost ;Port=5432")]
    [InlineData("HOST=LOCALHOST;Port=5432")]
    public void A_connection_string_pointing_at_the_local_machine_stops_a_real_deployment(string connectionString)
    {
        var configuration = ConfigurationWith(("RentalsDatabase", connectionString));

        var exception = Should.Throw<InsecureConfigurationException>(
            () => ConfigurationGuard.EnsureNoDevelopmentCredentials(configuration, "Production"));

        // El mensaje tiene que decir que variable falta: quien lo lea estara
        // mirando un contenedor que no arranca, probablemente con prisa.
        exception.Message.ShouldContain("ConnectionStrings__RentalsDatabase");
    }

    [Fact]
    public void A_real_connection_string_lets_the_service_start()
    {
        var configuration = ConfigurationWith(
            ("RentalsDatabase", "Host=rentals.abc123.eu-west-1.rds.amazonaws.com;Port=5432;Database=rentals"));

        Should.NotThrow(() => ConfigurationGuard.EnsureNoDevelopmentCredentials(configuration, "Production"));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void The_local_environments_are_left_alone(string environment)
    {
        // Apuntar a localhost es exactamente lo correcto aqui: son las
        // credenciales del PostgreSQL del compose.
        var configuration = ConfigurationWith(("RentalsDatabase", "Host=localhost;Password=rentals"));

        Should.NotThrow(() => ConfigurationGuard.EnsureNoDevelopmentCredentials(configuration, environment));
    }

    [Fact]
    public void Every_connection_string_is_checked_not_only_the_first()
    {
        var configuration = ConfigurationWith(
            ("Primary", "Host=produccion.rds.amazonaws.com;Database=uno"),
            ("Secondary", "Host=localhost;Database=dos"));

        var exception = Should.Throw<InsecureConfigurationException>(
            () => ConfigurationGuard.EnsureNoDevelopmentCredentials(configuration, "Production"));

        exception.Message.ShouldContain("Secondary");
    }

    [Fact]
    public void A_service_without_any_database_is_not_affected()
    {
        var configuration = new ConfigurationBuilder().Build();

        Should.NotThrow(() => ConfigurationGuard.EnsureNoDevelopmentCredentials(configuration, "Production"));
    }

    [Fact]
    public void An_empty_connection_string_is_not_treated_as_a_local_one()
    {
        var configuration = ConfigurationWith(("RentalsDatabase", string.Empty));

        Should.NotThrow(() => ConfigurationGuard.EnsureNoDevelopmentCredentials(configuration, "Production"));
    }
}

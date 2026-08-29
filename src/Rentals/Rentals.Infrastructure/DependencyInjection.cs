using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Rentals.Application.Abstractions;
using Rentals.Infrastructure.Http;
using Rentals.Infrastructure.Messaging;
using Rentals.Infrastructure.Persistence;
using Rentals.Infrastructure.Time;

namespace Rentals.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddRentalsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<KafkaOptions>(configuration.GetSection(KafkaOptions.SectionName));
        services.Configure<ServiceEndpointsOptions>(configuration.GetSection(ServiceEndpointsOptions.SectionName));

        var endpoints = configuration.GetSection(ServiceEndpointsOptions.SectionName).Get<ServiceEndpointsOptions>()
                        ?? new ServiceEndpointsOptions();

        services.AddDbContext<RentalsDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("RentalsDatabase"),
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", RentalsDbContext.Schema)));

        services.AddScoped<IRentalRepository, EfRentalRepository>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IEventPublisher, KafkaEventPublisher>();

        services.AddHttpClient<IVehicleCatalog, FleetHttpVehicleCatalog>(client =>
            {
                client.BaseAddress = new Uri(endpoints.FleetBaseUrl);
                client.Timeout = TimeSpan.FromSeconds(endpoints.TimeoutSeconds);
            })
            .AddResilience(endpoints);

        services.AddHttpClient<IPricingCalculator, PricingHttpCalculator>(client =>
            {
                client.BaseAddress = new Uri(endpoints.PricingBaseUrl);
                client.Timeout = TimeSpan.FromSeconds(endpoints.TimeoutSeconds);
            })
            .AddResilience(endpoints);

        return services;
    }

    /// <summary>
    /// Reintentos cortos ante fallos transitorios (5xx y errores de red).
    /// Los valores salen de configuracion para que las pruebas puedan hacerlos
    /// casi instantaneos y verificar el comportamiento sin esperar segundos.
    /// </summary>
    private static IHttpClientBuilder AddResilience(this IHttpClientBuilder builder, ServiceEndpointsOptions endpoints)
    {
        builder.AddResilienceHandler("transient", pipeline =>
        {
            pipeline.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = endpoints.MaxRetryAttempts,
                Delay = TimeSpan.FromMilliseconds(endpoints.RetryDelayMilliseconds),
                BackoffType = DelayBackoffType.Constant,
                UseJitter = false
            });

            pipeline.AddTimeout(TimeSpan.FromSeconds(endpoints.TimeoutSeconds));
        });

        return builder;
    }
}

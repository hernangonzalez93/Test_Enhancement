using Microsoft.Extensions.DependencyInjection;
using Rentals.Application.Rentals;

namespace Rentals.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddRentalsApplication(this IServiceCollection services)
    {
        services.AddScoped<IRentalService, RentalService>();
        return services;
    }
}

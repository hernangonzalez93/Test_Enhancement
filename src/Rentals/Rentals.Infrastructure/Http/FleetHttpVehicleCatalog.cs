using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Rentals.Application.Abstractions;
using Rentals.Domain.Model;

namespace Rentals.Infrastructure.Http;

/// <summary>
/// Adaptador HTTP hacia el servicio Fleet.
/// Traduce el protocolo a lenguaje de aplicacion: 404 no es un error, es
/// "no existe"; cualquier otro fallo si es una indisponibilidad tecnica.
/// </summary>
public sealed class FleetHttpVehicleCatalog(HttpClient httpClient, ILogger<FleetHttpVehicleCatalog> logger)
    : IVehicleCatalog
{
    public const string HttpClientName = "fleet";

    public async Task<VehicleSnapshot?> FindAsync(VehicleId vehicleId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync($"/api/vehicles/{vehicleId.Value}", cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Fleet returned {StatusCode} for vehicle {VehicleId}.", response.StatusCode, vehicleId);
                throw new ExternalServiceUnavailableException("fleet");
            }

            var payload = await response.Content.ReadFromJsonAsync<FleetVehicleResponse>(cancellationToken);

            return payload is null
                ? null
                : new VehicleSnapshot(
                    payload.Id,
                    payload.Model,
                    payload.VehicleClass,
                    payload.DailyRate,
                    payload.Currency,
                    payload.Available);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            logger.LogError(exception, "Fleet service call failed for vehicle {VehicleId}.", vehicleId);
            throw new ExternalServiceUnavailableException("fleet", exception);
        }
    }

    private sealed record FleetVehicleResponse(
        Guid Id,
        string Model,
        string VehicleClass,
        decimal DailyRate,
        string Currency,
        bool Available);
}

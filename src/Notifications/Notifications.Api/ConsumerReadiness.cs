using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Notifications.Api;

/// <summary>
/// Señal de que el consumidor de Kafka ya recibió particiones y por tanto está
/// consumiendo de verdad.
///
/// Sin esto, `/health/ready` respondía 200 en cuanto PostgreSQL era accesible,
/// y `docker compose up -d --wait` daba el servicio por listo mientras el
/// consumidor seguía uniéndose a su grupo. Las pruebas E2E arrancaban entonces
/// contra un sistema que aún no reaccionaba a los eventos: se observaron más de
/// 13 segundos entre publicar un evento y procesarlo.
/// </summary>
public sealed class ConsumerReadiness
{
    private volatile bool _ready;

    public bool IsReady => _ready;

    public void MarkReady() => _ready = true;
}

public sealed class KafkaConsumerHealthCheck(ConsumerReadiness readiness) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(readiness.IsReady
            ? HealthCheckResult.Healthy("Kafka consumer has partitions assigned.")
            : HealthCheckResult.Unhealthy("Kafka consumer has not joined its group yet."));
}

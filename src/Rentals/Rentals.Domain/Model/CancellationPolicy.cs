namespace Rentals.Domain.Model;

/// <summary>
/// Servicio de dominio sin estado: cuanto se devuelve al cancelar, segun la
/// antelacion respecto al inicio de la renta. Al no tener dependencias es
/// trivialmente parametrizable con [Theory].
/// </summary>
public static class CancellationPolicy
{
    public const decimal FullRefundPercentage = 100m;
    public const decimal HalfRefundPercentage = 50m;
    public const decimal PartialRefundPercentage = 25m;
    public const decimal NoRefundPercentage = 0m;

    public static decimal RefundPercentageFor(DateTimeOffset periodStart, DateTimeOffset cancelledAt)
    {
        var hoursAhead = (periodStart.ToUniversalTime() - cancelledAt.ToUniversalTime()).TotalHours;

        return hoursAhead switch
        {
            >= 48 => FullRefundPercentage,
            >= 24 => HalfRefundPercentage,
            >= 2 => PartialRefundPercentage,
            _ => NoRefundPercentage
        };
    }

    public static Money RefundFor(Money total, DateTimeOffset periodStart, DateTimeOffset cancelledAt)
    {
        ArgumentNullException.ThrowIfNull(total);
        return total.Percentage(RefundPercentageFor(periodStart, cancelledAt));
    }
}

namespace Rentals.Infrastructure.Http;

public sealed class ServiceEndpointsOptions
{
    public const string SectionName = "Services";

    public string PricingBaseUrl { get; set; } = "http://localhost:5102";

    public string FleetBaseUrl { get; set; } = "http://localhost:5103";

    public int TimeoutSeconds { get; set; } = 5;

    public int MaxRetryAttempts { get; set; } = 2;

    public int RetryDelayMilliseconds { get; set; } = 50;
}

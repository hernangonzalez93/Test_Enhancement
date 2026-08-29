namespace Rentals.Infrastructure.Messaging;

public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = "localhost:9092";

    public string RentalEventsTopic { get; set; } = Shared.Contracts.KafkaTopics.RentalEvents;

    /// <summary>Milisegundos que espera el productor antes de fallar la publicacion.</summary>
    public int MessageTimeoutMs { get; set; } = 5000;
}

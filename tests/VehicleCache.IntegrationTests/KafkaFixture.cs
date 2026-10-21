using Confluent.Kafka;
using Testcontainers.Kafka;

namespace VehicleCache.IntegrationTests;

/// <summary>A real single-node Kafka for consumer tests (offset-store semantics cannot be faked meaningfully).</summary>
public sealed class KafkaFixture : IAsyncLifetime
{
    private readonly KafkaContainer _container = new KafkaBuilder("confluentinc/cp-kafka:7.9.1").Build();

    public string BootstrapServers => _container.GetBootstrapAddress();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Produces values in order to partition 0 of a fresh single-partition topic. <c>null</c> = tombstone.</summary>
    public async Task ProduceAsync(string topic, IEnumerable<byte[]?> values)
    {
        using var producer = new ProducerBuilder<Null, byte[]?>(new ProducerConfig { BootstrapServers = BootstrapServers })
            .SetValueSerializer(Serializers.ByteArray)
            .Build();
        foreach (var value in values)
        {
            await producer.ProduceAsync(new TopicPartition(topic, 0), new Message<Null, byte[]?> { Value = value });
        }

        producer.Flush(TimeSpan.FromSeconds(10));
    }

    public async Task<long> CommittedOffsetAsync(string groupId, string topic)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = BootstrapServers }).Build();
        var result = await admin.ListConsumerGroupOffsetsAsync(
            [new ConsumerGroupTopicPartitions(groupId, [new TopicPartition(topic, 0)])]);
        return result.Single().Partitions.Single().Offset.Value;
    }
}

using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VehicleCache.Projection.Debezium;
using VehicleCache.Worker.Consumers;

namespace VehicleCache.IntegrationTests;

/// <summary>The real consumer loop against real Kafka + Valkey: offsets are stored only after the write; poison stops it.</summary>
[Collection("valkey")]
public sealed class ConsumerTests(ValkeyFixture valkey, KafkaFixture kafka) : IClassFixture<KafkaFixture>, IAsyncLifetime
{
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public Task InitializeAsync() => valkey.FlushAsync();

    public Task DisposeAsync()
    {
        Environment.ExitCode = 0; // the consumer sets 1 on poison, by design
        return Task.CompletedTask;
    }

    private static byte[]? FixtureValue(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(FixturesDir, $"{name}.json"));
        return bytes.Length == 0 ? null : bytes;
    }

    private sealed class TestLifetime : IHostApplicationLifetime
    {
        private readonly TaskCompletionSource _stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StopRequested => _stopRequested.Task;

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => _stopRequested.TrySetResult();
    }

    /// <summary>Runs one consumer until it asks the host to stop (poison) and returns after it closed.</summary>
    private async Task RunUntilStopAsync(string topic, string groupId)
    {
        var lifetime = new TestLifetime();
        var metrics = new ProjectionMetrics();
        var handler = new CdcRecordHandler(topic, VehicleChangeMapper.Map, valkey.Writer, metrics, NullLogger.Instance);
        var options = Options.Create(new KafkaOptions
        {
            BootstrapServers = kafka.BootstrapServers,
            GroupId = groupId,
            AutoCommitIntervalMs = 100,
        });
        using var consumer = new KafkaCdcConsumer(handler, options, lifetime, metrics, NullLogger<KafkaCdcConsumer>.Instance);

        await consumer.StartAsync(CancellationToken.None);
        await lifetime.StopRequested.WaitAsync(TimeSpan.FromSeconds(60));
        await consumer.StopAsync(CancellationToken.None);
        Environment.ExitCode.ShouldBe(1);
    }

    [Fact]
    [Trait("TB", "16")]
    public async Task Poison_record_stops_without_storing_offset_and_is_redelivered()
    {
        var topic = $"vehicles-{Guid.NewGuid():N}";
        var groupId = $"group-{Guid.NewGuid():N}";
        await kafka.ProduceAsync(topic,
        [
            FixtureValue("vehicles.insert"),     // offset 0: vehicle 1 v1
            FixtureValue("vehicles.update"),     // offset 1: vehicle 1 v2
            FixtureValue("vehicles.delete"),     // offset 2: vehicle 3 deleted
            FixtureValue("vehicles.tombstone"),  // offset 3: null value → skipped, still stored
            Encoding.UTF8.GetBytes("not json"),  // offset 4: poison
            FixtureValue("vehicles.insert"),     // offset 5: must never be reached
        ]);

        await RunUntilStopAsync(topic, groupId);

        (await kafka.CommittedOffsetAsync(groupId, topic)).ShouldBe(4); // next to read = the poison record
        (await valkey.HashAsync(1))["stateVersion"].ShouldBe("2");
        (await valkey.HashAsync(3)).ShouldBe(new Dictionary<string, string> { ["deleted"] = "1" });

        // A restarted worker resumes at the poison record again: it is never skipped.
        Environment.ExitCode = 0;
        await RunUntilStopAsync(topic, groupId);

        (await kafka.CommittedOffsetAsync(groupId, topic)).ShouldBe(4);
    }
}

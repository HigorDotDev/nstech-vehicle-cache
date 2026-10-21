using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VehicleCache.Projection;
using VehicleCache.Projection.Debezium;
using VehicleCache.Worker.Cache;
using VehicleCache.Worker.Consumers;

namespace VehicleCache.UnitTests;

public class CdcRecordHandlerTests
{
    private sealed class FakeWriter(params Func<CacheCommand, WriteOutcome>[] responses) : IVehicleCacheWriter
    {
        public List<CacheCommand> Calls { get; } = [];

        public Task<WriteOutcome> ApplyAsync(CacheCommand command)
        {
            Calls.Add(command);
            return Task.FromResult(responses[Math.Min(Calls.Count, responses.Length) - 1](command));
        }
    }

    private static CdcRecordHandler Handler(IVehicleCacheWriter writer, ProjectionMetrics metrics) =>
        new("fleet.public.vehicles", VehicleChangeMapper.Map, writer, metrics, NullLogger.Instance);

    [Fact]
    [Trait("TB", "16")]
    public async Task Poison_record_throws_before_touching_the_cache()
    {
        var writer = new FakeWriter(_ => WriteOutcome.Applied);

        await Should.ThrowAsync<CdcFormatException>(() =>
            Handler(writer, new ProjectionMetrics()).HandleAsync(Encoding.UTF8.GetBytes("not json"), CancellationToken.None));

        writer.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Transient_valkey_failure_is_retried_then_recorded()
    {
        var attempts = 0;
        var writer = new FakeWriter(_ => ++attempts < 3
            ? throw new TimeoutException("valkey timeout")
            : WriteOutcome.Applied);
        var metrics = new ProjectionMetrics();

        var outcome = await Handler(writer, metrics).HandleAsync(Fixture.Value("vehicles.insert"), CancellationToken.None);

        outcome.ShouldBe(WriteOutcome.Applied);
        writer.Calls.Count.ShouldBe(3);
        metrics.Summary("fleet.public.vehicles").ShouldBe("applied=1 rejected=0 skipped=0");
    }

    [Fact]
    public async Task Outcomes_are_counted_per_topic()
    {
        var metrics = new ProjectionMetrics();
        var handler = Handler(new FakeWriter(c => c is Skip ? WriteOutcome.Skipped : WriteOutcome.Rejected), metrics);

        await handler.HandleAsync(Fixture.Value("vehicles.update"), CancellationToken.None);
        await handler.HandleAsync(null, CancellationToken.None);

        metrics.Summary("fleet.public.vehicles").ShouldBe("applied=0 rejected=1 skipped=1");
    }
}

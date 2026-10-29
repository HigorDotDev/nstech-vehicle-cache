using StackExchange.Redis;
using VehicleCache.Projection;
using VehicleCache.Projection.Debezium;
using VehicleCache.Worker.Cache;

namespace VehicleCache.Worker.Consumers;

/// <summary>
/// One CDC record → one cache outcome. Kafka-agnostic so it can be unit-tested; the consumer decides about offsets.
/// </summary>
/// <exception cref="CdcFormatException">Poison record: the caller must stop without storing the offset (ADR-0005).</exception>
public sealed class CdcRecordHandler(
    string topic,
    Func<byte[]?, CacheCommand> map,
    IVehicleCacheWriter writer,
    ProjectionMetrics metrics,
    ILogger logger)
{
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5)];

    public string Topic { get; } = topic;

    public async Task<WriteOutcome> HandleAsync(byte[]? value, CancellationToken cancellationToken)
    {
        var command = map(value); // throws CdcFormatException on poison — never caught here

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var outcome = await writer.ApplyAsync(command);
                metrics.Record(Topic, outcome);
                logger.LogDebug("{Topic} {Command} {Outcome}", Topic, command, outcome);
                return outcome;
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException && attempt < RetryDelays.Length)
            {
                // Retrying is safe: every script is idempotent (ADR-0003).
                logger.LogWarning(ex, "Valkey write failed on {Topic}, retry {Attempt}", Topic, attempt + 1);
                await Task.Delay(RetryDelays[attempt], cancellationToken);
            }
        }
    }
}

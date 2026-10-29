using Confluent.Kafka;
using Microsoft.Extensions.Options;
using VehicleCache.Projection.Debezium;

namespace VehicleCache.Worker.Consumers;

public sealed class KafkaOptions
{
    public const string Section = "Kafka";

    public required string BootstrapServers { get; init; }

    public string GroupId { get; init; } = "vehicle-cache";

    public string VehiclesTopic { get; init; } = "fleet.public.vehicles";

    public string PositionsTopic { get; init; } = "fleet.public.positions";

    public int AutoCommitIntervalMs { get; init; } = 1000;
}

/// <summary>
/// One instance per CDC topic (spec §7). At-least-once (ADR-0005): the offset is stored only after the
/// Valkey script returned; the background auto-commit only commits stored offsets. A poison record stops
/// the host with a non-zero exit code and is never stored, so it is redelivered after the fix.
/// </summary>
public sealed class KafkaCdcConsumer(
    CdcRecordHandler handler,
    IOptions<KafkaOptions> options,
    IHostApplicationLifetime lifetime,
    ProjectionMetrics metrics,
    TimeProvider time,
    ILogger<KafkaCdcConsumer> logger) : BackgroundService
{
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(10);

    // Confluent's Consume() blocks; run the loop on its own thread so StartAsync is not held up.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => RunAsync(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            .Unwrap();

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var config = new ConsumerConfig
        {
            BootstrapServers = settings.BootstrapServers,
            GroupId = settings.GroupId,
            ClientId = $"vehicle-cache-{handler.Topic}",
            EnableAutoCommit = true,
            EnableAutoOffsetStore = false,
            AutoCommitIntervalMs = settings.AutoCommitIntervalMs,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky,
        };

        using var consumer = new ConsumerBuilder<Ignore, byte[]?>(config)
            .SetValueDeserializer(Deserializers.ByteArray)
            .SetPartitionsAssignedHandler((_, partitions) =>
                logger.LogInformation("{Topic} assigned {Partitions}", handler.Topic, string.Join(",", partitions.Select(p => p.Partition.Value))))
            .SetPartitionsRevokedHandler((_, partitions) =>
                logger.LogInformation("{Topic} revoked {Partitions}", handler.Topic, string.Join(",", partitions.Select(p => p.Partition.Value))))
            .Build();

        consumer.Subscribe(handler.Topic);
        logger.LogInformation("Consuming {Topic} as group {GroupId}", handler.Topic, settings.GroupId);

        var lastSummary = time.GetTimestamp(); // log cadence only — never written to the cache
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<Ignore, byte[]?>? result;
                try
                {
                    result = consumer.Consume(TimeSpan.FromMilliseconds(500));
                }
                catch (ConsumeException ex) when (!ex.Error.IsFatal)
                {
                    // e.g. the topic does not exist yet because the connector is not registered: keep polling.
                    logger.LogWarning("{Topic} consume error: {Reason}", handler.Topic, ex.Error.Reason);
                    continue;
                }

                if (time.GetElapsedTime(lastSummary) >= SummaryInterval)
                {
                    logger.LogInformation("Projection {Topic} {Summary}", handler.Topic, metrics.Summary(handler.Topic));
                    lastSummary = time.GetTimestamp();
                }

                if (result is null || result.IsPartitionEOF)
                {
                    continue;
                }

                try
                {
                    await handler.HandleAsync(result.Message.Value, stoppingToken);
                }
                catch (CdcFormatException ex)
                {
                    logger.LogCritical(ex,
                        "Poison record on {Topic} partition {Partition} offset {Offset}; stopping without storing its offset",
                        result.Topic, result.Partition.Value, result.Offset.Value);
                    Environment.ExitCode = 1;
                    lifetime.StopApplication();
                    return;
                }

                consumer.StoreOffset(result); // only after the write returned (Applied, Rejected or Skipped)
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // graceful shutdown
        }
        catch (Exception ex)
        {
            // Valkey still failing after retries, fatal Kafka error, …: crash and let Kubernetes restart us.
            logger.LogCritical(ex, "Consumer for {Topic} failed", handler.Topic);
            Environment.ExitCode = 1;
            lifetime.StopApplication();
        }
        finally
        {
            logger.LogInformation("Projection {Topic} {Summary} (final)", handler.Topic, metrics.Summary(handler.Topic));
            consumer.Close(); // commits stored offsets; a force-killed pod never gets here (TB-12)
        }
    }
}

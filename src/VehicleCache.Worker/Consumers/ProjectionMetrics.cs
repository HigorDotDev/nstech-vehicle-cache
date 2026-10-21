using System.Collections.Concurrent;
using VehicleCache.Worker.Cache;

namespace VehicleCache.Worker.Consumers;

/// <summary>Applied / rejected / skipped counters per topic. Logged only — never written to Valkey (invariant 2).</summary>
public sealed class ProjectionMetrics
{
    private readonly ConcurrentDictionary<(string Topic, WriteOutcome Outcome), long> _counts = new();

    public void Record(string topic, WriteOutcome outcome) =>
        _counts.AddOrUpdate((topic, outcome), 1, (_, count) => count + 1);

    public long Get(string topic, WriteOutcome outcome) => _counts.GetValueOrDefault((topic, outcome));

    /// <summary>e.g. <c>applied=120 rejected=37 skipped=4</c> — the e2e summary greps this.</summary>
    public string Summary(string topic) =>
        $"applied={Get(topic, WriteOutcome.Applied)} rejected={Get(topic, WriteOutcome.Rejected)} skipped={Get(topic, WriteOutcome.Skipped)}";
}

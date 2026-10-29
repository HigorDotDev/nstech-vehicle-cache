namespace VehicleCache.MockWriters;

/// <summary>Paces statements to a target rate (0 = unlimited) so e2e chaos steps can hit a worker mid-stream.</summary>
public sealed class Throttle(double perSecond, TimeProvider time)
{
    private readonly long _start = time.GetTimestamp();
    private long _count;

    public async Task WaitAsync(CancellationToken ct)
    {
        if (perSecond <= 0)
        {
            return;
        }

        var due = TimeSpan.FromSeconds(++_count / perSecond);
        var elapsed = time.GetElapsedTime(_start);
        if (due > elapsed)
        {
            await Task.Delay(due - elapsed, time, ct);
        }
    }
}

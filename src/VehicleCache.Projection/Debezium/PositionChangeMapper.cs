namespace VehicleCache.Projection.Debezium;

/// <summary>Maps a <c>fleet.public.positions</c> record value to a cache command (spec §4 table).</summary>
public static class PositionChangeMapper
{
    public const string Table = "positions";

    /// <param name="value">Record value; <c>null</c> is a Kafka tombstone.</param>
    /// <exception cref="CdcFormatException">The value is not a valid positions change event.</exception>
    public static CacheCommand Map(byte[]? value)
    {
        if (value is null)
        {
            return new Skip("Kafka tombstone");
        }

        var envelope = DebeziumEnvelopeParser.Parse(value, DebeziumJsonContext.Default.PositionEnvelope, Table);

        return envelope.Op switch
        {
            "r" or "c" => envelope.After is { } row
                ? new ApplyPosition(
                    row.VehicleId,
                    row.Id,
                    CdcTimestamps.ToEpochMicros(row.RecordedAt),
                    CdcTimestamps.Normalize(row.RecordedAt),
                    row.Lat,
                    row.Lng)
                : throw new CdcFormatException($"op '{envelope.Op}' without 'after'."),
            // Positions are append-only, enforced by a trigger at the source (spec §3). An update or delete would move
            // the argmax in a way the cache cannot follow without history, so it is poison, not something to ignore.
            _ => throw new CdcFormatException($"Unsupported op '{envelope.Op}' on {Table}: positions are append-only."),
        };
    }
}

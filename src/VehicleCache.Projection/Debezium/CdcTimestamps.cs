using System.Globalization;

namespace VehicleCache.Projection.Debezium;

/// <summary>
/// Canonical timestamp handling (spec §5). Values are re-formatted, not stored "as emitted", so the cache does not
/// depend on Debezium's formatting and the verifier can produce the same string from Npgsql values.
/// </summary>
public static class CdcTimestamps
{
    public const string CanonicalFormat = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'";

    /// <summary>Debezium <c>ZonedTimestamp</c> (ISO-8601) → epoch microseconds. Exact: 1 tick = 100 ns.</summary>
    public static long ToEpochMicros(string isoTimestamp) =>
        (Parse(isoTimestamp).UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / TimeSpan.TicksPerMicrosecond;

    /// <summary>Any ISO-8601 UTC timestamp → <see cref="CanonicalFormat"/> (always 6 fractional digits, Z).</summary>
    public static string Normalize(string isoTimestamp) => Format(Parse(isoTimestamp));

    public static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(CanonicalFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string isoTimestamp) =>
        DateTimeOffset.TryParse(
            isoTimestamp,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : throw new CdcFormatException($"Invalid timestamp '{isoTimestamp}'.");
}

using System.Text;
using VehicleCache.Projection;
using VehicleCache.Projection.Debezium;

namespace VehicleCache.UnitTests;

public class PositionChangeMapperTests
{
    private static long EpochMicros(int hour, int minute, int second, int micros) =>
        new DateTimeOffset(2026, 10, 21, hour, minute, second, TimeSpan.Zero).ToUnixTimeMilliseconds() * 1000 + micros;

    [Fact]
    [Trait("TB", "02")]
    public void Insert_maps_to_apply_position_with_exact_microseconds()
    {
        PositionChangeMapper.Map(Fixture.Value("positions.insert"))
            .ShouldBe(new ApplyPosition(1, 1, EpochMicros(10, 6, 0, 123_456), "2026-10-21T10:06:00.123456Z", -18.9186, -48.2772));
    }

    [Fact]
    [Trait("TB", "03")]
    public void Late_position_has_higher_id_but_older_recorded_at()
    {
        var late = PositionChangeMapper.Map(Fixture.Value("positions.insert-late")).ShouldBeOfType<ApplyPosition>();
        var newer = PositionChangeMapper.Map(Fixture.Value("positions.insert")).ShouldBeOfType<ApplyPosition>();

        late.PositionId.ShouldBeGreaterThan(newer.PositionId);
        late.RecordedAtUs.ShouldBeLessThan(newer.RecordedAtUs);
    }

    [Fact]
    [Trait("TB", "07")]
    public void Orphan_position_maps_like_any_other()
    {
        PositionChangeMapper.Map(Fixture.Value("positions.insert-orphan"))
            .ShouldBe(new ApplyPosition(2, 3, EpochMicros(10, 2, 0, 0), "2026-10-21T10:02:00.000000Z", -19.0, -48.3));
    }

    [Fact]
    public void Arguments_are_formatted_as_stored_in_valkey()
    {
        PositionChangeMapper.Map(Fixture.Value("positions.insert-orphan")).ShouldBeOfType<ApplyPosition>()
            .Arguments().ShouldBe(["1792576920000000", "3", "-19", "-48.3", "2026-10-21T10:02:00.000000Z"]);
    }

    [Fact]
    public void Null_value_is_kafka_tombstone()
    {
        PositionChangeMapper.Map(null).ShouldBeOfType<Skip>();
    }

    [Theory]
    [Trait("TB", "16")]
    [InlineData("""{"op":"u","before":null,"after":{"id":1,"vehicle_id":1,"lat":1.0,"lng":2.0,"recorded_at":"2026-10-21T10:00:00Z"},"source":{"table":"positions"}}""")]
    [InlineData("""{"op":"d","before":{"id":1},"after":null,"source":{"table":"positions"}}""")]
    public void Update_or_delete_of_append_only_positions_is_poison(string json)
    {
        Should.Throw<CdcFormatException>(() => PositionChangeMapper.Map(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("2026-10-21T10:00:00Z", "2026-10-21T10:00:00.000000Z")]
    [InlineData("2026-10-21T10:00:00.5Z", "2026-10-21T10:00:00.500000Z")]
    [InlineData("2026-10-21T07:00:00.123456-03:00", "2026-10-21T10:00:00.123456Z")]
    public void Timestamps_are_normalized_to_canonical_format(string input, string expected)
    {
        CdcTimestamps.Normalize(input).ShouldBe(expected);
    }

    [Fact]
    [Trait("TB", "16")]
    public void Invalid_timestamp_is_poison()
    {
        const string json = """{"op":"c","before":null,"after":{"id":1,"vehicle_id":1,"lat":1.0,"lng":2.0,"recorded_at":"yesterday"},"source":{"table":"positions"}}""";
        Should.Throw<CdcFormatException>(() => PositionChangeMapper.Map(Encoding.UTF8.GetBytes(json)));
    }
}

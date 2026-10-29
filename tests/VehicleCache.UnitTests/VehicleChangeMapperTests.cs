using System.Text;
using VehicleCache.Projection;
using VehicleCache.Projection.Debezium;

namespace VehicleCache.UnitTests;

public class VehicleChangeMapperTests
{
    [Fact]
    public void Insert_maps_to_upsert_with_row_version()
    {
        VehicleChangeMapper.Map(Fixture.Value("vehicles.insert"))
            .ShouldBe(new UpsertState(1, 1, "REGISTERED", "ABC1D23", "2026-10-21T10:00:00.000000Z"));
    }

    [Fact]
    [Trait("TB", "01")]
    public void Update_maps_to_upsert_with_trigger_bumped_version()
    {
        VehicleChangeMapper.Map(Fixture.Value("vehicles.update"))
            .ShouldBe(new UpsertState(1, 2, "ACTIVE", "ABC1D23", "2026-10-21T10:05:00.000000Z"));
    }

    [Theory]
    [Trait("TB", "11")]
    [InlineData("vehicles.snapshot-first", 1, 2, "ACTIVE", "ABC1D23", "2026-10-21T10:05:00.000000Z")]
    [InlineData("vehicles.snapshot-last-in-collection", 2, 1, "REGISTERED", "XYZ9K88", "2026-10-21T10:03:00.000000Z")]
    public void Snapshot_read_maps_to_upsert(string fixture, long id, long version, string state, string plate, string changedAt)
    {
        VehicleChangeMapper.Map(Fixture.Value(fixture))
            .ShouldBe(new UpsertState(id, version, state, plate, changedAt));
    }

    [Fact]
    [Trait("TB", "08")]
    public void Delete_uses_only_before_id_because_other_columns_are_zero_filled()
    {
        VehicleChangeMapper.Map(Fixture.Value("vehicles.delete")).ShouldBe(new DeleteVehicle(3));
    }

    [Fact]
    [Trait("TB", "08")]
    public void Null_value_is_kafka_tombstone()
    {
        Fixture.Value("vehicles.tombstone").ShouldBeNull();
        VehicleChangeMapper.Map(null).ShouldBeOfType<Skip>();
    }

    [Theory]
    [Trait("TB", "16")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"op":"c","before":null,"after":null,"source":{"table":"vehicles"}}""")]
    [InlineData("""{"op":"t","before":null,"after":null,"source":{"table":"vehicles"}}""")]
    [InlineData("""{"op":"d","before":null,"after":null,"source":{"table":"vehicles"}}""")]
    [InlineData("""{"op":"c","before":null,"after":{"id":1,"plate":"A","state":"ACTIVE","state_changed_at":"2026-10-21T10:00:00Z"},"source":{"table":"vehicles"}}""")]
    public void Malformed_events_are_poison(string json)
    {
        Should.Throw<CdcFormatException>(() => VehicleChangeMapper.Map(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    [Trait("TB", "16")]
    public void Event_from_another_table_is_poison()
    {
        Should.Throw<CdcFormatException>(() => VehicleChangeMapper.Map(Fixture.Value("positions.insert")));
    }
}

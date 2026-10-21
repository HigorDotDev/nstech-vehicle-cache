using System.Reflection;

namespace VehicleCache.Projection;

/// <summary>The three conditional-write scripts (spec §6), embedded from <c>Lua/*.lua</c>.</summary>
public static class LuaScripts
{
    public static string ApplyState { get; } = Load("apply_state.lua");
    public static string ApplyPosition { get; } = Load("apply_position.lua");
    public static string DeleteVehicle { get; } = Load("delete_vehicle.lua");

    private static string Load(string fileName)
    {
        var name = $"VehicleCache.Projection.Lua.{fileName}";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded Lua script '{name}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

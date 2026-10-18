using System.Linq;
using System.Numerics;
using Content.Server.Administration;
using Content.Shared.Administration;
using Content.Shared.Station.Components;
using Robust.Shared.Console;
using Robust.Shared.Map;

namespace Content.Server.Fps3D;

/// <summary>
///     Dev/test helper: teleport yourself to grid-local coordinates on the station's main grid (e.g. positions taken
///     from the map file). Server-side because only the server knows which grid is the station.
/// </summary>
[AdminCommand(AdminFlags.Admin)]
public sealed partial class Fps3DTpStationCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "fps3d_tpstation";
    public string Description => "Teleport to grid-local coordinates on the station's largest grid.";
    public string Help => "Usage: fps3d_tpstation <x> <y>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (args.Length != 2
            || !float.TryParse(args[0], inv, out var x)
            || !float.TryParse(args[1], inv, out var y)
            || shell.Player?.AttachedEntity is not { } player)
        {
            shell.WriteLine(Help);
            return;
        }

        var stations = _entities.System<Content.Shared.Station.Systems.StationSystem>();
        var xforms = _entities.System<SharedTransformSystem>();
        var query = _entities.EntityQueryEnumerator<StationDataComponent>();
        while (query.MoveNext(out var station, out var data))
        {
            if (stations.GetLargestGrid((station, data)) is not { } grid)
                continue;

            var world = Vector2.Transform(new Vector2(x, y), xforms.GetWorldMatrix(grid));
            xforms.SetMapCoordinates(player, new MapCoordinates(world, _entities.GetComponent<TransformComponent>(grid).MapID));
            shell.WriteLine($"Teleported to {world} on {_entities.ToPrettyString(grid)}.");
            return;
        }

        shell.WriteLine("No station.");
    }
}

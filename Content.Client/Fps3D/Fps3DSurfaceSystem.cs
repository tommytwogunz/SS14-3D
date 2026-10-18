using Robust.Client.Graphics.World3D;

namespace Content.Client.Fps3D;

/// <summary>
///     Maps SS14's floor tiles and walls to the stylized material library (Resources/Models3D/Materials) so the
///     environment has one cohesive look instead of pixel art. Each surface keeps its own colour: the engine tints the
///     material with the average colour of the tile or wall it replaces.
/// </summary>
public sealed partial class Fps3DSurfaceSystem : EntitySystem
{
    [Dependency] private IWorld3DManager _world3D = default!;

    private readonly Dictionary<string, string> _tileCache = new();
    private readonly Dictionary<string, string> _wallCache = new();

    /// <summary>Keyword in the tile ID (lowercase) → material. First match wins, so specific words go first.</summary>
    private static readonly (string Word, string Material)[] TileWords =
    {
        ("carpet", "floor_carpet"),
        ("wood", "floor_wood"),
        ("grass", "floor_grass"),
        ("astro", "floor_grass"),
        ("snow", "floor_snow"),
        ("ice", "floor_snow"),
        ("asteroid", "floor_rock"),
        ("rock", "floor_rock"),
        ("cave", "floor_rock"),
        ("basalt", "floor_rock"),
        ("dirt", "floor_rock"),
        ("desert", "floor_rock"),
        ("sand", "floor_rock"),
        ("planet", "floor_rock"),
        ("concrete", "floor_concrete"),
        ("lattice", "floor_grating"),
        ("grating", "floor_grating"),
        ("grate", "floor_grating"),
        ("catwalk", "floor_grating"),
        ("circuit", "floor_tech"),
        ("tech", "floor_tech"),
        ("reinforced", "floor_diamond"),
        ("diamond", "floor_diamond"),
        ("plating", "floor_diamond"),
        ("hull", "floor_diamond"),
        ("checker", "floor_checker"),
        ("freezer", "floor_checker"),
        ("bar", "floor_checker"),
        ("kitchen", "floor_checker"),
        ("clown", "floor_checker"),
        ("mime", "floor_checker"),
        ("laundry", "floor_tiles"),
        ("white", "floor_tiles"),
        ("tile", "floor_tiles"),
        ("lino", "floor_tiles"),
        ("medical", "floor_tiles"),
        ("showroom", "floor_tiles"),
        ("glass", "floor_tiles"),
    };

    public override void Initialize()
    {
        base.Initialize();
        _world3D.TileMaterial = TileMaterial;
        _world3D.WallMaterial = WallMaterial;
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _world3D.TileMaterial = null;
        _world3D.WallMaterial = null;
    }

    private string? TileMaterial(string tileId)
    {
        if (_tileCache.TryGetValue(tileId, out var cached))
            return cached;

        var id = tileId.ToLowerInvariant();
        var material = "floor_plates"; // station steel, dark/mono/plastic/everything else
        foreach (var (word, mat) in TileWords)
        {
            if (id.Contains(word))
            {
                material = mat;
                break;
            }
        }

        _tileCache[tileId] = material;
        return material;
    }

    private string? WallMaterial(EntityUid uid)
    {
        if (!TryComp(uid, out MetaDataComponent? meta) || meta.EntityPrototype is not { } proto)
            return "wall_panel";

        if (_wallCache.TryGetValue(proto.ID, out var cached))
            return cached;

        var id = proto.ID.ToLowerInvariant();
        var material = id.Contains("reinforced") || id.Contains("plastitanium") || id.Contains("shuttle")
            ? "wall_reinforced"
            : id.Contains("wood") ? "floor_wood"
            : id.Contains("rock") || id.Contains("asteroid") || id.Contains("cave") || id.Contains("basalt") ? "floor_rock"
            : id.Contains("ice") || id.Contains("snow") ? "floor_snow"
            : "wall_panel";

        _wallCache[proto.ID] = material;
        return material;
    }
}

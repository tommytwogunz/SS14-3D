using System.Numerics;
using Content.Shared.Fps3D;
using Content.Shared.Humanoid;
using Content.Shared.Lock;
using Robust.Client.GameObjects;
using Robust.Shared.Physics.Components;
using Robust.Shared.Timing;
using Content.Shared.Storage.Components;
using Robust.Client.Graphics.World3D;
using Robust.Shared.Prototypes;

namespace Content.Client.Fps3D;

/// <summary>
///     Picks real 3D meshes (Resources/Models3D, built by tools/models) for common entities, by prototype
///     inheritance, and poses/colours them from game state (doors open, lock lights, ...). Everything else keeps the
///     model generated from its sprite.
/// </summary>
public sealed partial class Fps3DModelSystem : EntitySystem
{
    [Dependency] private IWorld3DManager _world3D = default!;
    [Dependency] private IPrototypeManager _proto = default!;
    [Dependency] private SpriteSystem _sprite = default!;
    [Dependency] private Content.Shared.Inventory.InventorySystem _inventory = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedMapSystem _map = default!;

    /// <summary>
    ///     Base prototype → model and scale. Walking up the entity's parents, the closest listed base wins; an empty
    ///     model keeps the sprite model.
    /// </summary>
    private static readonly (string Base, string Model, float Scale)[] ModelsByBase =
    {
        ("LockerBase", "locker", 1f),
        ("ClosetBase", "locker", 1f),
        ("CrateBaseWeldable", "crate", 1f),
        ("CrateBaseSecure", "crate", 1f),
        ("VendingMachine", "vending", 1f),
        ("BaseComputer", "computer", 1f),
        ("ComfyChair", "armchair", 1f),
        ("OfficeChairBase", "officechair", 1f),
        ("StoolBase", "stool", 1f),
        ("SteelBench", "bench", 1f),
        ("WoodenBench", "bench", 1f),
        ("ChairBase", "chair", 1f),
        ("UnanchoredChairBase", "chair", 1f),
        ("Bed", "bed", 1f),
        ("GasCanister", "canister", 1f),
        ("PottedPlantBase", "plant", 1f),
        ("TableBase", "table", 1f),
        // Floor machines: a cabinet showing the machine's own sprite on its front.
        ("BaseLathe", "machine", 1f),
        ("ChemMaster", "machine", 1f),
        ("ChemDispenser", "machine", 1f),
        ("BaseSMES", "machine", 1f),
        ("BaseSubstation", "machine", 1f),
        ("ResearchAndDevelopmentServer", "machine", 1f),
        ("CloningPod", "machine", 1f),
        ("MedicalScanner", "machine", 1f),
        // Animals: archetype models scaled per species.
        ("MobCorgiBase", "quadruped", 0.75f),
        ("MobCat", "quadruped", 0.6f),
        ("MobFox", "quadruped", 0.65f),
        ("MobPig", "quadruped", 0.85f),
        ("MobPossum", "quadruped", 0.5f),
        ("MobRaccoon", "quadruped", 0.55f),
        ("MobGoat", "quadruped", 1.0f),
        ("MobCow", "quadruped", 1.45f),
        ("MobMouse", "quadruped", 0.28f),
        ("MobHamster", "quadruped", 0.25f),
        ("MobLizard", "quadruped", 0.4f),
        ("MobChicken", "bird", 0.7f),
        ("MobDuckMallard", "bird", 0.7f),
        ("BaseMobCarp", "fish", 1f),
    };

    private readonly Dictionary<string, (string? Model, float Scale)> _modelByProto = new();
    private readonly Dictionary<EntityUid, World3DModelRequest> _requests = new();
    private readonly Dictionary<EntityUid, (float Phase, float Swing, TimeSpan Updated)> _walk = new();

    private static readonly Vector4 DefaultSkin = new(0.86f, 0.68f, 0.56f, 1f);

    private static readonly Vector4 LampLocked = new(1f, 0.15f, 0.12f, 1f);
    private static readonly Vector4 LampUnlocked = new(0.2f, 1f, 0.3f, 1f);
    private static readonly Vector4 LampOff = new(0.15f, 0.15f, 0.15f, 1f);

    public override void Initialize()
    {
        base.Initialize();
        _world3D.ModelProvider = GetModel;
        SubscribeLocalEvent<MetaDataComponent, ComponentShutdown>(OnShutdown);
        _proto.PrototypesReloaded += _ => _modelByProto.Clear();
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _world3D.ModelProvider = null;
    }

    private void OnShutdown(EntityUid uid, MetaDataComponent comp, ComponentShutdown args)
    {
        _requests.Remove(uid);
        _walk.Remove(uid);
    }

    private World3DModelRequest? GetModel(EntityUid uid)
    {
        if (!TryComp(uid, out MetaDataComponent? meta) || meta.EntityPrototype is not { } proto)
            return null;

        if (!_modelByProto.TryGetValue(proto.ID, out var found))
        {
            found = FindModel(proto);
            _modelByProto[proto.ID] = found;
        }

        // Characters: any mob drawn with the humanoid body layers (monkeys are small ones).
        if (found.Model == null && HasComp<Content.Shared.Mobs.Components.MobStateComponent>(uid) && TryComp(uid, out SpriteComponent? sprite)
            && _sprite.LayerMapTryGet((uid, sprite), HumanoidVisualLayers.Chest, out _, false))
        {
            var monkey = System.Linq.Enumerable.Any(_proto.EnumerateAllParents<EntityPrototype>(proto.ID, includeSelf: true),
                p => p.id == "MobBaseMonkey");
            found = ("human", monkey ? 0.6f : 1f);
            _modelByProto[proto.ID] = found;
        }

        if (found.Model is not { } model)
            return null;

        if (!_requests.TryGetValue(uid, out var request))
        {
            request = new World3DModelRequest { Model = model, Scale = found.Scale };
            _requests[uid] = request;
        }

        switch (model)
        {
            case "human":
                UpdateHuman(uid, request);
                break;
            case "quadruped":
            {
                var (phase, swing, time) = Walk(uid, 3.5f / found.Scale);
                var s = MathF.Sin(phase) * swing;
                request.Bones["legFL"] = new Vector3(s, 0, 0);
                request.Bones["legBR"] = new Vector3(s, 0, 0);
                request.Bones["legFR"] = new Vector3(-s, 0, 0);
                request.Bones["legBL"] = new Vector3(-s, 0, 0);
                request.Bones["tail"] = new Vector3(0.3f, 0, MathF.Sin(time * 6f) * 0.35f);
                request.Bones["head"] = new Vector3(MathF.Sin(phase * 2f) * swing * 0.1f, 0, 0);
                break;
            }
            case "bird":
            {
                var (phase, swing, time) = Walk(uid, 6f);
                var s = MathF.Sin(phase) * swing;
                request.Bones["legL"] = new Vector3(s, 0, 0);
                request.Bones["legR"] = new Vector3(-s, 0, 0);
                request.Bones["head"] = new Vector3(MathF.Sin(phase * 2f) * swing * 0.4f, 0, 0);
                var flap = swing * MathF.Sin(time * 14f) * 0.4f;
                request.Bones["wingL"] = new Vector3(0, -flap, 0);
                request.Bones["wingR"] = new Vector3(0, flap, 0);
                break;
            }
            case "fish":
            {
                var (_, swing, time) = Walk(uid, 2f);
                var sway = MathF.Sin(time * (4f + swing * 8f)) * (0.25f + swing * 0.3f);
                request.Bones["tail"] = new Vector3(0, 0, sway);
                request.Bones["finL"] = new Vector3(0, MathF.Sin(time * 5f) * 0.3f, 0);
                request.Bones["finR"] = new Vector3(0, -MathF.Sin(time * 5f) * 0.3f, 0);
                break;
            }
            case "table":
                UpdateTableLegs(uid, request);
                break;
            case "crate":
                var crateOpen = TryComp(uid, out EntityStorageComponent? crateStorage) && crateStorage.Open;
                request.Bones["lid"] = new Vector3(crateOpen ? -1.9f : 0f, 0, 0);
                break;
            case "computer":
                request.ColorLayers[1] = Content.Client.Computer.Visualizers.ComputerVisualLayers.Screen;
                break;
            case "locker":
                var open = TryComp(uid, out EntityStorageComponent? storage) && storage.Open;
                request.Bones["door"] = new Vector3(0, 0, open ? -1.75f : 0f);
                request.Colors[0] = TryComp(uid, out LockComponent? lockComp)
                    ? lockComp.Locked ? LampLocked : LampUnlocked
                    : LampOff;
                break;
        }

        return request;
    }

    private (string? Model, float Scale) FindModel(EntityPrototype proto)
    {
        // Includes abstract parents (most bases, e.g. LockerBase, are abstract).
        foreach (var (parentId, _) in _proto.EnumerateAllParents<EntityPrototype>(proto.ID, includeSelf: true))
        {
            foreach (var (baseId, model, scale) in ModelsByBase)
            {
                if (parentId == baseId)
                    return (model.Length > 0 ? model : null, scale);
            }
        }

        return (null, 1f);
    }

    /// <summary>
    ///     Walk cycle for an entity: phase advances with distance moved (<paramref name="stride"/> radians per metre),
    ///     swing (0..0.7) eases in and out with speed; also returns a running time for idle motion.
    /// </summary>
    private (float Phase, float Swing, float Time) Walk(EntityUid uid, float stride)
    {
        var now = _timing.RealTime;
        _walk.TryGetValue(uid, out var walk);
        var dt = walk.Updated == TimeSpan.Zero ? 0f : (float) Math.Min((now - walk.Updated).TotalSeconds, 0.1);
        var speed = TryComp(uid, out PhysicsComponent? body) ? body.LinearVelocity.Length() : 0f;
        walk.Phase += speed * dt * stride;
        var targetSwing = Math.Clamp(speed / 4.5f, 0f, 1f) * 0.7f;
        walk.Swing += (targetSwing - walk.Swing) * (1f - MathF.Exp(-dt * 10f));
        walk.Updated = now;
        _walk[uid] = walk;
        // Offset idle motion per entity so a room of animals doesn't move in lockstep.
        return (walk.Phase, walk.Swing, (float) now.TotalSeconds + uid.Id % 97 * 0.37f);
    }

    /// <summary>
    ///     Joined tables share corners: a table draws a corner leg only when none of the three neighbouring tiles that
    ///     meet at that corner holds a table, so a run of tables is one surface with legs at its outer corners.
    /// </summary>
    private void UpdateTableLegs(EntityUid uid, World3DModelRequest request)
    {
        var xform = Transform(uid);
        if (xform.GridUid is not { } grid || !TryComp(grid, out Robust.Shared.Map.Components.MapGridComponent? gridComp))
            return;

        var tile = _map.TileIndicesFor(grid, gridComp, xform.Coordinates);

        bool Table(int dx, int dy)
        {
            var anchored = _map.GetAnchoredEntitiesEnumerator(grid, gridComp, tile + new Vector2i(dx, dy));
            while (anchored.MoveNext(out var other))
            {
                if (TryComp(other, out MetaDataComponent? m) && m.EntityPrototype is { } p
                    && (_modelByProto.TryGetValue(p.ID, out var f) ? f : FindModel(p)).Model == "table")
                {
                    return true;
                }
            }

            return false;
        }

        // Model corners are in the table's own frame; tables are anchored unrotated on the grid.
        SetHidden(request, "legNE", Table(1, 0) || Table(0, 1) || Table(1, 1));
        SetHidden(request, "legNW", Table(-1, 0) || Table(0, 1) || Table(-1, 1));
        SetHidden(request, "legSE", Table(1, 0) || Table(0, -1) || Table(1, -1));
        SetHidden(request, "legSW", Table(-1, 0) || Table(0, -1) || Table(-1, -1));
    }

    /// <summary>
    ///     Character colours come from what the sprite actually draws (body layers, worn clothing layers), so species,
    ///     skin tone and outfits carry over. Limbs swing with walking speed; the head follows the look pitch.
    /// </summary>
    private void UpdateHuman(EntityUid uid, World3DModelRequest request)
    {
        var sprite = Comp<SpriteComponent>(uid);
        object skin = VisibleLayer(uid, sprite, HumanoidVisualLayers.Chest) ?? (object) HumanoidVisualLayers.Head;
        var jumpsuit = VisibleLayer(uid, sprite, "jumpsuit");
        var outer = VisibleLayer(uid, sprite, "outerClothing");

        request.Colors[0] = DefaultSkin;
        request.ColorLayers[0] = skin;
        request.ColorLayers[1] = outer ?? jumpsuit ?? skin;
        request.ColorLayers[2] = jumpsuit ?? outer ?? skin;
        request.ColorLayers[3] = VisibleLayer(uid, sprite, "shoes") ?? skin;
        var hair = VisibleLayer(uid, sprite, HumanoidVisualLayers.Hair);
        // Hats and backpacks: coloured from the worn item's own sprite (their body layers can be overlays).
        _inventory.TryGetSlotEntity(uid, "head", out var hat);
        _inventory.TryGetSlotEntity(uid, "back", out var pack);
        request.ColorLayers[4] = hair ?? skin;
        request.ColorEntities[5] = hat;
        request.ColorLayers[6] = VisibleLayer(uid, sprite, "gloves") ?? skin;
        request.ColorEntities[7] = pack;
        for (var i = 1; i < 8; i++)
            request.Colors[i] = DefaultSkin;

        SetHidden(request, "hat", hat == null);
        SetHidden(request, "hair", hair == null || hat != null);
        SetHidden(request, "pack", pack == null);

        var (phase, swing, _) = Walk(uid, 2.6f / request.Scale);
        var s = MathF.Sin(phase) * swing;
        request.Bones["legL"] = new Vector3(s, 0, 0);
        request.Bones["legR"] = new Vector3(-s, 0, 0);
        request.Bones["armL"] = new Vector3(-s * 0.8f, 0, 0);
        request.Bones["armR"] = new Vector3(s * 0.8f, 0, 0);

        var pitch = TryComp(uid, out Height3DComponent? height) && float.IsFinite(height.LookPitch) ? height.LookPitch : 0f;
        request.Bones["head"] = new Vector3(Math.Clamp(-pitch * 0.7f, -0.6f, 0.6f), 0, 0);
    }

    private static void SetHidden(World3DModelRequest request, string group, bool hidden)
    {
        if (hidden)
            request.Hidden.Add(group);
        else
            request.Hidden.Remove(group);
    }

    private static readonly string[] SlotBookmarks =
        { "jumpsuit", "outerClothing", "shoes", "head", "gloves", "back", "belt", "neck", "mask", "eyes", "ears", "id", "pocket1", "pocket2", "suitstorage" };

    /// <summary>
    ///     The layer key if that layer draws something now. Slot and hair keys are empty bookmarks with the real layer
    ///     inserted right after them, so a bookmark counts when the next layer has a state and isn't another bookmark.
    /// </summary>
    private object? VisibleLayer(EntityUid uid, SpriteComponent sprite, object key)
    {
        if (!TryLayerIndex(uid, sprite, key, out var index))
            return null;

        if (sprite[index] is SpriteComponent.Layer { ActualState: not null } own)
            return own.Visible ? key : null;

        var next = index + 1;
        if (next >= System.Linq.Enumerable.Count(sprite.AllLayers) || sprite[next] is not SpriteComponent.Layer { Visible: true, ActualState: not null })
            return null;

        foreach (var bookmark in SlotBookmarks)
        {
            if (TryLayerIndex(uid, sprite, bookmark, out var b) && b == next)
                return null;
        }

        foreach (var layerKey in Enum.GetValues<HumanoidVisualLayers>())
        {
            if (TryLayerIndex(uid, sprite, layerKey, out var b) && b == next)
                return null;
        }

        return key;
    }

    private bool TryLayerIndex(EntityUid uid, SpriteComponent sprite, object key, out int index)
        => key is Enum e
            ? _sprite.LayerMapTryGet((uid, sprite), e, out index, false)
            : _sprite.LayerMapTryGet((uid, sprite), (string) key, out index, false);
}

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

    /// <summary>Base prototype → model. The first base found walking up the entity's parents wins.</summary>
    private static readonly (string Base, string Model)[] ModelsByBase =
    {
        ("LockerBase", "locker"),
        ("ClosetBase", "locker"),
        ("CrateBaseWeldable", "crate"),
        ("CrateBaseSecure", "crate"),
        ("VendingMachine", "vending"),
        ("BaseComputer", "computer"),
        ("ComfyChair", "armchair"),
        ("OfficeChairBase", "officechair"),
        ("StoolBase", "stool"),
        ("SteelBench", ""), // benches are wide: keep their sprite model
        ("WoodenBench", ""),
        ("ChairBase", "chair"),
        ("UnanchoredChairBase", "chair"),
        ("Bed", "bed"),
        ("GasCanister", "canister"),
        ("PottedPlantBase", "plant"),
    };

    private readonly Dictionary<string, string?> _modelByProto = new();
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

        if (!_modelByProto.TryGetValue(proto.ID, out var model))
        {
            model = FindModel(proto);
            _modelByProto[proto.ID] = model;
        }

        // Characters: any mob drawn with the humanoid body layers.
        if (model == null && HasComp<Content.Shared.Mobs.Components.MobStateComponent>(uid) && TryComp(uid, out SpriteComponent? sprite)
            && _sprite.LayerMapTryGet((uid, sprite), HumanoidVisualLayers.Chest, out _, false))
        {
            model = "human";
            _modelByProto[proto.ID] = model;
        }

        if (model == null)
            return null;

        if (!_requests.TryGetValue(uid, out var request))
        {
            request = new World3DModelRequest { Model = model };
            _requests[uid] = request;
        }

        switch (model)
        {
            case "human":
                UpdateHuman(uid, request);
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

    private string? FindModel(EntityPrototype proto)
    {
        // Includes abstract parents (most bases, e.g. LockerBase, are abstract).
        foreach (var (parentId, _) in _proto.EnumerateAllParents<EntityPrototype>(proto.ID, includeSelf: true))
        {
            foreach (var (baseId, model) in ModelsByBase)
            {
                if (parentId == baseId)
                    return model.Length > 0 ? model : null;
            }
        }

        return null;
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

        // Walk cycle: phase advances with distance moved; swing eases in and out with speed.
        var now = _timing.RealTime;
        _walk.TryGetValue(uid, out var walk);
        var dt = walk.Updated == TimeSpan.Zero ? 0f : (float) Math.Min((now - walk.Updated).TotalSeconds, 0.1);
        var speed = TryComp(uid, out PhysicsComponent? body) ? body.LinearVelocity.Length() : 0f;
        walk.Phase += speed * dt * 2.6f;
        var targetSwing = Math.Clamp(speed / 4.5f, 0f, 1f) * 0.7f;
        walk.Swing += (targetSwing - walk.Swing) * (1f - MathF.Exp(-dt * 10f));
        walk.Updated = now;
        _walk[uid] = walk;

        var s = MathF.Sin(walk.Phase) * walk.Swing;
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

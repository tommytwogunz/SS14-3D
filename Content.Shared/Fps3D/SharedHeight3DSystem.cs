using Content.Shared.Ghost.Components;
using Content.Shared.Gravity;
using Content.Shared.Input;
using Content.Shared.Item;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.Placeable;
using Content.Shared.Storage.Components;
using Content.Shared.Throwing;
using Robust.Shared.Containers;
using Robust.Shared.Input.Binding;
using Robust.Shared.Network;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Physics.Events;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Shared.Fps3D;

/// <summary>
///     Source-style vertical physics layered on SS14's 2D rigid-body physics: gravity, landing and bouncing,
///     standing on tables and props, jumping and crouching, height-aware collisions (things above an obstacle's top
///     pass over it), thrown items flying in arcs, and tall props tipping over when hit hard.
///     Numbers follow GMod: sv_gravity 600 u/s² ≈ 15.2 m/s² (1 u ≈ 2.54 cm).
/// </summary>
public sealed class SharedHeight3DSystem : EntitySystem
{
    [Dependency] private SharedGravitySystem _gravity = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedContainerSystem _container = default!;
    [Dependency] private MovementSpeedModifierSystem _speed = default!;
    [Dependency] private INetManager _net = default!;
    [Dependency] private ISharedPlayerManager _player = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedBroadphaseSystem _broadphase = default!;

    public const float Gravity = 15.2f;
    /// <summary>Peak ≈ 0.96 m: enough to get onto a 0.9 m table, like jumping onto a desk in GMod.</summary>
    public const float JumpSpeed = 5.4f;
    public const float MobHeight = 1.75f;
    public const float CrouchHeight = 1.15f;
    public const float TableTop = 0.9f;
    public const float MobStepUp = 0.35f;
    public const float ItemStepUp = 0.12f;
    /// <summary>Crouch-walk speed multiplier (GMod crouch is about a third of run speed; SS14 walk is already slow).</summary>
    public const float CrouchSpeed = 0.5f;

    private readonly HashSet<EntityUid> _nearby = new();
    private float _sweepTimer;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<Height3DComponent, PreventCollideEvent>(OnPreventCollide);
        SubscribeLocalEvent<Height3DComponent, StartCollideEvent>(OnStartCollide);
        SubscribeLocalEvent<Height3DComponent, ThrownEvent>(OnThrown);
        SubscribeLocalEvent<Height3DComponent, EntGotRemovedFromContainerMessage>(OnRemovedFromContainer);
        SubscribeLocalEvent<Height3DComponent, RefreshMovementSpeedModifiersEvent>(OnRefreshSpeed);
        SubscribeLocalEvent<Height3DComponent, MoveEvent>(OnMove);

        CommandBinds.Builder
            .Bind(ContentKeyFunctions.Jump3D, InputCmdHandler.FromDelegate(OnJump, handle: false, outsidePrediction: false))
            .Bind(ContentKeyFunctions.Crouch3D, InputCmdHandler.FromDelegate(
                session => SetCrouch(session, true), session => SetCrouch(session, false), handle: false, outsidePrediction: false))
            .Register<SharedHeight3DSystem>();
    }

    public override void Shutdown()
    {
        base.Shutdown();
        CommandBinds.Unregister<SharedHeight3DSystem>();
    }

    #region Setup

    /// <summary>
    ///     Server: give heights to mobs, items, tables and lockers that don't have one yet (map load, new spawns).
    ///     Done as a sweep because other systems already own MapInit on those components, and the event bus
    ///     allows only one subscription per component/event pair.
    /// </summary>
    private void AttachMissingHeights()
    {
        var movers = EntityQueryEnumerator<InputMoverComponent>();
        while (movers.MoveNext(out var uid, out _))
        {
            if (!HasComp<Height3DComponent>(uid))
                InitMover(uid);
        }

        var surfaces = EntityQueryEnumerator<PlaceableSurfaceComponent>();
        while (surfaces.MoveNext(out var uid, out _))
        {
            if (!HasComp<Height3DComponent>(uid))
                InitSurface(uid);
        }

        var storages = EntityQueryEnumerator<EntityStorageComponent>();
        while (storages.MoveNext(out var uid, out _))
        {
            if (!HasComp<Height3DComponent>(uid))
                InitStorage(uid);
        }

        // Items last, so the tables and props they rest on already have heights.
        var items = EntityQueryEnumerator<ItemComponent>();
        while (items.MoveNext(out var uid, out _))
        {
            if (!HasComp<Height3DComponent>(uid))
                InitItem(uid);
        }
    }

    private void InitMover(EntityUid ent)
    {
        if (HasComp<GhostComponent>(ent))
            return;

        var comp = EnsureComp<Height3DComponent>(ent);
        comp.Kind = Height3DKind.Mob;
        comp.Height = MobHeight;
        Dirty(ent, comp);
    }

    private void InitItem(EntityUid ent)
    {
        if (HasComp<InputMoverComponent>(ent))
            return;

        var comp = EnsureComp<Height3DComponent>(ent);
        comp.Kind = Height3DKind.Item;
        comp.Height = 0.15f;

        // Mapped items sitting on tables start on the table top.
        var xform = Transform(ent);
        if (!_container.IsEntityInContainer(ent))
            comp.Z = GroundHeight(ent, comp, xform, stepUp: 10f);
        comp.Awake = false;
        Dirty(ent, comp);
    }

    private void InitSurface(EntityUid ent)
    {
        if (!Transform(ent).Anchored || HasComp<ItemComponent>(ent))
            return;

        var comp = EnsureComp<Height3DComponent>(ent);
        comp.Kind = Height3DKind.Table;
        comp.Height = TableTop;
        comp.Awake = false;
        Dirty(ent, comp);
    }

    private void InitStorage(EntityUid ent)
    {
        // Lockers, closets, crates: loose large props.
        if (HasComp<ItemComponent>(ent) || Transform(ent).Anchored)
            return;

        var comp = EnsureComp<Height3DComponent>(ent);
        comp.Kind = Height3DKind.Prop;
        var id = MetaData(ent).EntityPrototype?.ID ?? "";
        comp.Height = id.Contains("Crate", StringComparison.OrdinalIgnoreCase) ? 0.8f : 1.7f;
        comp.Awake = false;
        Dirty(ent, comp);
    }

    #endregion

    #region Simulation

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Resting things above the floor are re-checked once a second, in case their support moved away.
        _sweepTimer += frameTime;
        var sweep = _sweepTimer >= 1f;
        if (sweep)
            _sweepTimer = 0f;

        if (sweep && !_net.IsClient)
            AttachMissingHeights();

        var isClient = _net.IsClient;
        var local = _player.LocalEntity;

        var query = EntityQueryEnumerator<Height3DComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var comp, out var xform))
        {
            if (comp.Kind == Height3DKind.Table)
                continue;

            // Clients only predict their own body; everything else comes from server state.
            if (isClient && uid != local)
                continue;

            if (!comp.Awake && comp.Kind != Height3DKind.Mob && !(sweep && comp.Z > 0.01f))
                continue;

            if (xform.MapUid == null || _container.IsEntityInContainer(uid))
                continue;

            Step((uid, comp, xform), frameTime);
        }
    }

    private void Step(Entity<Height3DComponent, TransformComponent> ent, float dt)
    {
        var (uid, comp, xform) = ent;
        var changed = false;

        if (_gravity.IsWeightless(uid))
        {
            // Zero-g: keep drifting vertically, slowly damped. No falling.
            if (MathF.Abs(comp.VZ) > 0.01f)
            {
                comp.Z = MathF.Max(0f, comp.Z + comp.VZ * dt);
                comp.VZ *= MathF.Max(0f, 1f - 0.5f * dt);
                changed = true;
            }
            else if (comp.Kind != Height3DKind.Mob)
            {
                comp.Awake = false;
            }
        }
        else
        {
            var ground = GroundHeight(uid, comp, xform);

            if (comp.Z > ground + 0.001f || comp.VZ > 0f)
            {
                comp.VZ -= Gravity * dt;
                comp.Z += comp.VZ * dt;
                if (comp.Kind == Height3DKind.Item)
                    comp.Spin += 12f * dt;
                changed = true;
            }

            if (comp.Z <= ground)
            {
                var impact = comp.VZ;
                comp.Z = ground;
                // Items bounce a little off hard landings; everything else stops.
                comp.VZ = comp.Kind == Height3DKind.Item && impact < -2.5f ? -impact * 0.3f : 0f;
                if (comp.VZ == 0f && comp.Spin != 0f)
                    comp.Spin = 0f;
                changed |= impact != 0f;
            }
        }

        // Tipping over: falls faster the further it leans, like a toppling box.
        if (comp.TiltVelocity != 0f)
        {
            comp.TiltVelocity += Gravity / MathF.Max(0.5f, comp.Height) * MathF.Sin(MathF.Max(comp.Tilt, 0.05f)) * dt;
            comp.Tilt += comp.TiltVelocity * dt;
            if (comp.Tilt >= MathF.PI / 2f)
            {
                comp.Tilt = MathF.PI / 2f;
                comp.TiltVelocity = 0f;
            }
            changed = true;
        }

        if (comp.Kind != Height3DKind.Mob && comp.VZ == 0f && comp.TiltVelocity == 0f)
            comp.Awake = false;

        if (changed)
        {
            Dirty(uid, comp);
            // Contacts are only filtered when created; re-run our height filter now that the height changed,
            // so a body rising above a table stops colliding with it (and collides again if it drops back).
            RefilterContacts(uid, xform);
        }
    }

    private void RefilterContacts(EntityUid uid, TransformComponent xform)
    {
        if (!TryComp<FixturesComponent>(uid, out var fixtures))
            return;

        foreach (var fixture in fixtures.Fixtures.Values)
        {
            _broadphase.Refilter(uid, fixture, xform);
        }
    }

    /// <summary>
    ///     Highest supporting surface under the entity that it can be standing on: the floor (0), table tops,
    ///     and the tops of props, counting only surfaces at most <paramref name="stepUp"/> above the entity's feet.
    /// </summary>
    public float GroundHeight(EntityUid uid, Height3DComponent comp, TransformComponent xform, float? stepUp = null)
    {
        var step = stepUp ?? (comp.Kind == Height3DKind.Mob ? MobStepUp : ItemStepUp);
        var radius = comp.Kind == Height3DKind.Mob ? 0.3f : 0.12f;
        var best = 0f;

        _nearby.Clear();
        _lookup.GetEntitiesInRange(xform.Coordinates, radius, _nearby, LookupFlags.Static | LookupFlags.Dynamic | LookupFlags.Sundries);
        foreach (var other in _nearby)
        {
            if (other == uid || !TryComp<Height3DComponent>(other, out var o))
                continue;
            if (o.Kind is not (Height3DKind.Table or Height3DKind.Prop) || _container.IsEntityInContainer(other))
                continue;

            var top = Top(o);
            if (top <= comp.Z + step && top > best)
                best = top;
        }

        return best;
    }

    /// <summary>Current height of the entity's top, accounting for crouching and lying on its side.</summary>
    public static float Top(Height3DComponent comp)
    {
        var height = comp.Crouching ? CrouchHeight : comp.Height;
        if (comp.Tilt > 0.3f)
            height = MathF.Min(height, 0.55f); // lying down
        return comp.Z + height;
    }

    #endregion

    #region Events

    private void OnPreventCollide(Entity<Height3DComponent> ent, ref PreventCollideEvent args)
    {
        // Things without a height (walls, machines, airlocks) always block. Between two things with heights,
        // whichever is entirely above the other's top passes over it: jump onto tables, throw over counters.
        if (args.Cancelled || !TryComp<Height3DComponent>(args.OtherEntity, out var other))
            return;

        if (ent.Comp.Z >= Top(other) - PassMargin(ent.Comp, other) || other.Z >= Top(ent.Comp) - PassMargin(other, ent.Comp))
            args.Cancelled = true;
    }

    /// <summary>
    ///     How far below <paramref name="below"/>'s top <paramref name="above"/> can be and still pass over it.
    ///     Mobs get their step-up height against surfaces (Source steps up 18 u ≈ 0.46 m), and then snap onto them.
    /// </summary>
    private static float PassMargin(Height3DComponent above, Height3DComponent below)
    {
        return above.Kind == Height3DKind.Mob && below.Kind is Height3DKind.Table or Height3DKind.Prop
            ? MobStepUp
            : 0.05f;
    }

    private void OnStartCollide(Entity<Height3DComponent> ent, ref StartCollideEvent args)
    {
        // Tall props tip over when something heavy and fast hits them.
        var comp = ent.Comp;
        if (comp.Kind != Height3DKind.Prop || comp.Tilt > 0f || comp.TiltVelocity != 0f || comp.Height < 1f)
            return;

        var velocity = args.OtherBody.LinearVelocity - args.OurBody.LinearVelocity;
        var speed = velocity.Length();
        if (speed < 6f || speed * args.OtherBody.Mass < 25f)
            return;

        comp.TiltDirection = velocity.ToWorldAngle();
        comp.TiltVelocity = 1.2f;
        comp.Awake = true;
        Dirty(ent);
    }

    private void OnThrown(Entity<Height3DComponent> ent, ref ThrownEvent args)
    {
        var comp = ent.Comp;
        var startZ = comp.Z + 0.5f;
        var pitch = 0f;
        if (args.User is { } user && TryComp<Height3DComponent>(user, out var thrower))
        {
            startZ = thrower.Z + (thrower.Crouching ? 0.7f : 1.25f);
            pitch = thrower.LookPitch;
        }

        var speed = TryComp<PhysicsComponent>(ent, out var body) ? body.LinearVelocity.Length() : 5f;
        comp.Z = MathF.Max(comp.Z, startZ);
        // Aim with camera pitch, plus a small lob so flat throws carry a few metres.
        comp.VZ = speed * MathF.Tan(Math.Clamp(pitch, -1.2f, 1.2f)) + 1.2f;
        comp.Spin = 0f;
        comp.Awake = true;
        Dirty(ent);
    }

    private void OnRemovedFromContainer(Entity<Height3DComponent> ent, ref EntGotRemovedFromContainerMessage args)
    {
        // Dropped or taken out: start at the holder's hand height and fall from there.
        var holder = args.Container.Owner;
        var z = 0.9f;
        if (TryComp<Height3DComponent>(holder, out var h))
            z = h.Kind == Height3DKind.Mob ? h.Z + (h.Crouching ? 0.5f : 0.9f) : h.Z + 0.3f;

        ent.Comp.Z = z;
        ent.Comp.VZ = 0f;
        ent.Comp.Awake = true;
        Dirty(ent);
    }

    private void OnMove(Entity<Height3DComponent> ent, ref MoveEvent args)
    {
        // Pushed or carried somewhere new: re-check what's underneath.
        if (ent.Comp.Kind is Height3DKind.Item or Height3DKind.Prop)
            ent.Comp.Awake = true;
    }

    private void OnRefreshSpeed(Entity<Height3DComponent> ent, ref RefreshMovementSpeedModifiersEvent args)
    {
        if (ent.Comp.Crouching)
            args.ModifySpeed(CrouchSpeed, CrouchSpeed);
    }

    private void OnJump(ICommonSession? session)
    {
        if (session?.AttachedEntity is not { } uid || !TryComp<Height3DComponent>(uid, out var comp)
            || _gravity.IsWeightless(uid))
            return;

        var ground = GroundHeight(uid, comp, Transform(uid));
        if (comp.Z > ground + 0.02f || MathF.Abs(comp.VZ) > 0.1f)
            return; // Already in the air.

        comp.VZ = JumpSpeed;
        comp.Awake = true;
        Dirty(uid, comp);
    }

    private void SetCrouch(ICommonSession? session, bool crouching)
    {
        if (session?.AttachedEntity is not { } uid || !TryComp<Height3DComponent>(uid, out var comp)
            || comp.Crouching == crouching)
            return;

        comp.Crouching = crouching;
        Dirty(uid, comp);
        _speed.RefreshMovementSpeedModifiers(uid);
    }

    /// <summary>Stores the first-person camera pitch used to aim throws.</summary>
    public void SetLookPitch(EntityUid uid, float pitch)
    {
        if (!TryComp<Height3DComponent>(uid, out var comp) || MathF.Abs(comp.LookPitch - pitch) < 0.02f)
            return;

        comp.LookPitch = pitch;
        Dirty(uid, comp);
    }

    #endregion
}

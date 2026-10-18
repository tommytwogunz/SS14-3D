using System.Numerics;
using Content.Shared.Actions;
using Content.Shared.Fps3D;
using Content.Shared.GameTicking;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Mobs.Components;
using Robust.Shared.Containers;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;

namespace Content.Server.Fps3D;

/// <summary>
///     Server side of the GMod-style physics gun: hands one to every spawning player (plus a hotbar action to
///     re-equip it), and while something is held, drags it to the holder's aim point every tick with a stiff spring
///     on SS14's 2D physics (so it still collides) plus the height layer for the vertical axis.
/// </summary>
public sealed partial class PhysGunSystem : EntitySystem
{
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedActionsSystem _actions = default!;
    [Dependency] private SharedContainerSystem _container = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedHeight3DSystem _height = default!;

    private const string PhysGunPrototype = "PhysGun";
    private const string PhysGunAction = "ActionPhysGun";
    private const float EyeStanding = 1.6f;
    private const float EyeCrouching = 1.0f;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawn);
        SubscribeLocalEvent<PhysGunSummonActionEvent>(OnSummon);

        SubscribeNetworkEvent<PhysGunGrabMessage>(OnGrab);
        SubscribeNetworkEvent<PhysGunReleaseMessage>(OnRelease);
        SubscribeNetworkEvent<PhysGunFreezeMessage>(OnFreeze);
        SubscribeNetworkEvent<PhysGunScrollMessage>(OnScroll);
        SubscribeNetworkEvent<PhysGunRotateMessage>(OnRotate);
    }

    #region Equip

    private void OnPlayerSpawn(PlayerSpawnCompleteEvent ev)
    {
        _actions.AddAction(ev.Mob, PhysGunAction);
        var gun = Spawn(PhysGunPrototype, Transform(ev.Mob).Coordinates);
        if (!_hands.TryPickupAnyHand(ev.Mob, gun, checkActionBlocker: false, animate: false))
            QueueDel(gun);
    }

    private void OnSummon(PhysGunSummonActionEvent ev)
    {
        if (ev.Handled)
            return;

        var user = ev.Performer;
        ev.Handled = true;

        // Already holding one: make that hand active.
        foreach (var held in _hands.EnumerateHeld(user))
        {
            if (!HasComp<PhysGunComponent>(held))
                continue;

            if (_hands.IsHolding(user, held, out var hand))
                _hands.TrySetActiveHand(user, hand);
            return;
        }

        // Somewhere on us (bag, belt, pocket): take it out; otherwise make a new one.
        EntityUid? gun = null;
        var query = EntityQueryEnumerator<PhysGunComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            if (IsCarriedBy(uid, user))
            {
                gun = uid;
                break;
            }
        }

        gun ??= Spawn(PhysGunPrototype, Transform(user).Coordinates);
        if (!_hands.TryPickupAnyHand(user, gun.Value, checkActionBlocker: false, animate: false) && !IsCarriedBy(gun.Value, user))
            QueueDel(gun.Value);
    }

    private bool IsCarriedBy(EntityUid item, EntityUid user)
    {
        var current = item;
        while (_container.TryGetContainingContainer((current, null, null), out var container))
        {
            if (container.Owner == user)
                return true;
            current = container.Owner;
        }

        return false;
    }

    #endregion

    #region Input

    /// <summary>The physics gun in the sender's active hand, if any.</summary>
    private bool TryGetGun(ICommonSession session, out EntityUid user, out Entity<PhysGunComponent> gun)
    {
        gun = default;
        user = default;
        if (session.AttachedEntity is not { } attached
            || !_hands.TryGetActiveItem(attached, out var item)
            || !TryComp<PhysGunComponent>(item, out var comp))
            return false;

        user = attached;
        gun = (item.Value, comp);
        return true;
    }

    private void OnGrab(PhysGunGrabMessage msg, EntitySessionEventArgs args)
    {
        if (!TryGetGun(args.SenderSession, out var user, out var gun) || !TryGetEntity(msg.Target, out var target))
            return;

        var uid = target.Value;
        if (uid == user || uid == gun.Owner || !CanGrab(uid))
            return;

        var userPos = _transform.GetWorldPosition(user);
        if ((_transform.GetWorldPosition(uid) - userPos).Length() > gun.Comp.MaxRange)
            return;

        Release(gun);

        var height = _height.EnsureHeight(uid);
        if (height.Frozen)
            SetFrozen(uid, height, false);

        height.Held = true;
        height.VZ = 0f;
        height.Awake = true;
        Dirty(uid, height);

        gun.Comp.Held = uid;
        gun.Comp.Distance = Math.Clamp(msg.Distance, gun.Comp.MinDistance, gun.Comp.MaxDistance);
        Dirty(gun);
    }

    private bool CanGrab(EntityUid uid)
    {
        // Like GMod: anything loose, never the world (walls, floors, anchored machines, grids).
        if (HasComp<MapGridComponent>(uid) || HasComp<MapComponent>(uid) || _container.IsEntityInContainer(uid))
            return false;

        var xform = Transform(uid);
        if (xform.Anchored)
            return false;

        return TryComp<PhysicsComponent>(uid, out var body)
               && (body.BodyType != BodyType.Static || TryComp<Height3DComponent>(uid, out var h) && h.Frozen);
    }

    private void OnRelease(PhysGunReleaseMessage msg, EntitySessionEventArgs args)
    {
        if (TryGetGun(args.SenderSession, out _, out var gun))
            Release(gun);
    }

    private void OnFreeze(PhysGunFreezeMessage msg, EntitySessionEventArgs args)
    {
        if (!TryGetGun(args.SenderSession, out _, out var gun) || gun.Comp.Held is not { } held)
            return;

        // People can be carried but not pinned in mid-air.
        if (TryComp<Height3DComponent>(held, out var height) && !HasComp<MobStateComponent>(held))
        {
            gun.Comp.Held = null;
            Dirty(gun);
            height.Held = false;
            SetFrozen(held, height, true);
            return;
        }

        Release(gun);
    }

    private void OnScroll(PhysGunScrollMessage msg, EntitySessionEventArgs args)
    {
        if (!TryGetGun(args.SenderSession, out _, out var gun) || gun.Comp.Held == null || !float.IsFinite(msg.Delta))
            return;

        // GMod moves faster the further away the prop already is.
        var step = Math.Clamp(msg.Delta, -5f, 5f) * MathF.Max(0.25f, gun.Comp.Distance * 0.15f);
        gun.Comp.Distance = Math.Clamp(gun.Comp.Distance + step, gun.Comp.MinDistance, gun.Comp.MaxDistance);
        Dirty(gun);
    }

    private void OnRotate(PhysGunRotateMessage msg, EntitySessionEventArgs args)
    {
        if (!TryGetGun(args.SenderSession, out var user, out var gun) || gun.Comp.Held is not { } held
            || !float.IsFinite(msg.Yaw) || !float.IsFinite(msg.Tilt))
            return;

        var yaw = Math.Clamp(msg.Yaw, -1f, 1f);
        var tilt = Math.Clamp(msg.Tilt, -1f, 1f);

        var xform = Transform(held);
        _transform.SetWorldRotation(held, _transform.GetWorldRotation(xform) + new Angle(yaw));

        var height = _height.EnsureHeight(held);
        // Tip towards or away from the holder, along the holder's view.
        if (TryComp<Height3DComponent>(user, out var holder) && float.IsFinite(holder.LookYaw))
            height.TiltDirection = new Angle(holder.LookYaw + MathF.PI); // 0 rad points south; +π = forward
        height.Tilt = Math.Clamp(height.Tilt + tilt, -MathF.PI, MathF.PI);
        height.TiltVelocity = 0f;
        Dirty(held, height);
    }

    #endregion

    private void Release(Entity<PhysGunComponent> gun)
    {
        if (gun.Comp.Held is not { } held)
            return;

        gun.Comp.Held = null;
        Dirty(gun);

        if (!TryComp<Height3DComponent>(held, out var height))
            return;

        // Keep the swing's momentum (2D velocity stays on the body, VZ on the height layer): flick to throw.
        height.Held = false;
        height.Awake = true;
        Dirty(held, height);
    }

    private void SetFrozen(EntityUid uid, Height3DComponent height, bool frozen)
    {
        height.Frozen = frozen;
        height.VZ = 0f;
        height.Awake = !frozen;
        Dirty(uid, height);

        if (TryComp<PhysicsComponent>(uid, out var body))
        {
            _physics.SetLinearVelocity(uid, Vector2.Zero, body: body);
            _physics.SetAngularVelocity(uid, 0f, body: body);
            _physics.SetBodyType(uid, frozen ? BodyType.Static : BodyType.Dynamic, body: body);
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<PhysGunComponent>();
        while (query.MoveNext(out var gunUid, out var gun))
        {
            if (gun.Held is not { } held)
                continue;

            // Dropped the gun, put it away, or the target is gone: let go.
            if (!Exists(held) || !_container.TryGetContainingContainer((gunUid, null, null), out var container)
                || !_hands.IsHolding(container.Owner, gunUid) || _container.IsEntityInContainer(held))
            {
                Release((gunUid, gun));
                continue;
            }

            Hold((gunUid, gun), container.Owner, held, frameTime);
        }
    }

    private void Hold(Entity<PhysGunComponent> gun, EntityUid user, EntityUid held, float dt)
    {
        if (!TryComp<Height3DComponent>(user, out var holder) || !TryComp<Height3DComponent>(held, out var height))
            return;

        var yaw = float.IsFinite(holder.LookYaw) ? holder.LookYaw : (float) (_transform.GetWorldRotation(user) - Math.PI).Theta;
        var pitch = holder.LookPitch;

        // Aim point: from the holder's eye along the view, at the grab distance.
        var userPos = _transform.GetWorldPosition(user);
        var eyeZ = holder.Z + (holder.Crouching ? EyeCrouching : EyeStanding);
        var cosP = MathF.Cos(pitch);
        var dir = new Vector3(-MathF.Sin(yaw) * cosP, MathF.Cos(yaw) * cosP, MathF.Sin(pitch));
        var aim = new Vector3(userPos, eyeZ) + dir * gun.Comp.Distance;

        var xform = Transform(held);
        var pos = _transform.GetWorldPosition(xform);

        // Horizontal: stiff spring through SS14 physics, so the held thing still bumps into walls and people.
        if (TryComp<PhysicsComponent>(held, out var body))
        {
            var velocity = (new Vector2(aim.X, aim.Y) - pos) * gun.Comp.Stiffness;
            if (velocity.Length() > gun.Comp.MaxSpeed)
                velocity = velocity.Normalized() * gun.Comp.MaxSpeed;
            _physics.SetLinearVelocity(held, velocity, body: body);
            _physics.SetAngularVelocity(held, 0f, body: body);
        }

        // Vertical: hold the object's middle on the aim point, never below the floor.
        var targetZ = MathF.Max(0f, aim.Z - height.Height / 2f);
        height.VZ = Math.Clamp((targetZ - height.Z) * gun.Comp.Stiffness, -gun.Comp.MaxSpeed, gun.Comp.MaxSpeed);
        height.Z = MathF.Max(0f, height.Z + height.VZ * dt);
        Dirty(held, height);
        _height.RefilterContacts(held, xform);
    }
}

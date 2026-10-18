using Content.Shared.Movement.Components;
using Robust.Shared.Serialization;

namespace Content.Shared.Fps3D;

/// <summary>
///     First-person look direction, shared so movement prediction matches the server.
///     WASD is rotated by the mover's grid angle + <see cref="InputMoverComponent.RelativeRotation"/>;
///     setting that rotation to the camera yaw makes "forward" go where the player is looking.
/// </summary>
public sealed class SharedFps3DLookSystem : EntitySystem
{
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedHeight3DSystem _height = default!;
    [Dependency] private Robust.Shared.Network.INetManager _net = default!;
    [Dependency] private Robust.Shared.Player.ISharedPlayerManager _player = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<Fps3DLookMessage>(OnLook);
    }

    private void OnLook(Fps3DLookMessage msg, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is { } uid)
        {
            SetLookYaw(uid, msg.Yaw);
            if (float.IsFinite(msg.Pitch))
                _height.SetLookPitch(uid, Math.Clamp(msg.Pitch, -1.57f, 1.57f));
        }
    }

    /// <summary>Remembers the look yaw; <see cref="Update"/> keeps the mover pointed along it.</summary>
    public void SetLookYaw(EntityUid uid, float yaw)
    {
        if (!float.IsFinite(yaw) || !TryComp<Height3DComponent>(uid, out var comp))
            return;

        if (!float.IsFinite(comp.LookYaw) || MathF.Abs(comp.LookYaw - yaw) > 0.0005f)
        {
            comp.LookYaw = yaw;
            Dirty(uid, comp);
        }

        ApplyYaw(uid, yaw);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Re-assert every tick: SS14's mover snaps/lerps its relative rotation when you change grids, which
        // would otherwise drift WASD away from the camera until the next look message.
        var local = _player.LocalEntity;
        var query = EntityQueryEnumerator<Height3DComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.Kind != Height3DKind.Mob || !float.IsFinite(comp.LookYaw))
                continue;
            if (_net.IsClient && uid != local)
                continue;

            ApplyYaw(uid, comp.LookYaw);
        }
    }

    /// <summary>
    ///     Points movement input (and the mob's facing) along <paramref name="yaw"/>:
    ///     radians, 0 = north (+Y), counter-clockwise positive.
    /// </summary>
    public void ApplyYaw(EntityUid uid, float yaw)
    {
        if (!float.IsFinite(yaw) || !TryComp<InputMoverComponent>(uid, out var mover))
            return;

        var relativeTo = mover.RelativeEntity is { } rel && Exists(rel)
            ? _transform.GetWorldRotation(rel)
            : Angle.Zero;

        var relative = (new Angle(yaw) - relativeTo).FlipPositive();
        if (!mover.RelativeRotation.EqualsApprox(relative, 0.001) || !mover.TargetRelativeRotation.EqualsApprox(relative, 0.001))
        {
            // Set both so the mover doesn't lerp towards an old target.
            mover.RelativeRotation = relative;
            mover.TargetRelativeRotation = relative;
            Dirty(uid, mover);
        }

        // Face the look direction. Entity rotation 0 faces south, so add half a turn.
        var facing = new Angle(yaw + MathF.PI);
        if (!_transform.GetWorldRotation(uid).EqualsApprox(facing, 0.01))
            _transform.SetWorldRotation(uid, facing);
    }
}

/// <summary>Client → server: the first-person camera yaw for the sender's attached entity.</summary>
[Serializable, NetSerializable]
public sealed class Fps3DLookMessage(float yaw, float pitch) : EntityEventArgs
{
    public float Yaw = yaw;
    public float Pitch = pitch;
}

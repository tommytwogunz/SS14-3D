using Content.Shared.Actions;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared.Fps3D;

/// <summary>
///     GMod-style physics gun. While its holder keeps left mouse down, the grabbed entity floats along the holder's
///     view at <see cref="Distance"/>; E + mouse rotates it, right-click freezes it, letting go keeps its momentum.
///     Input arrives as client → server messages (see <see cref="PhysGunGrabMessage"/> and friends).
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class PhysGunComponent : Component
{
    /// <summary>The entity currently held, if any.</summary>
    [DataField, AutoNetworkedField]
    public EntityUid? Held;

    /// <summary>Distance from the holder's eye to the held entity, metres (mouse wheel changes it).</summary>
    [DataField, AutoNetworkedField]
    public float Distance = 2f;

    /// <summary>How far away something can be grabbed.</summary>
    [DataField]
    public float MaxRange = 40f;

    [DataField]
    public float MinDistance = 0.8f;

    [DataField]
    public float MaxDistance = 30f;

    /// <summary>How hard the held entity is pulled towards the aim point (1/s); GMod's physgun is very stiff.</summary>
    [DataField]
    public float Stiffness = 14f;

    /// <summary>Top speed the held entity is dragged at, m/s.</summary>
    [DataField]
    public float MaxSpeed = 30f;
}

[Serializable, NetSerializable]
public sealed class PhysGunGrabMessage(NetEntity target, float distance) : EntityEventArgs
{
    public NetEntity Target = target;
    public float Distance = distance;
}

[Serializable, NetSerializable]
public sealed class PhysGunReleaseMessage : EntityEventArgs;

[Serializable, NetSerializable]
public sealed class PhysGunFreezeMessage : EntityEventArgs;

/// <summary>Mouse wheel while holding: positive pushes the held entity away.</summary>
[Serializable, NetSerializable]
public sealed class PhysGunScrollMessage(float delta) : EntityEventArgs
{
    public float Delta = delta;
}

/// <summary>E + mouse while holding: spin about the vertical axis and tip towards/away from the holder (radians).</summary>
[Serializable, NetSerializable]
public sealed class PhysGunRotateMessage(float yaw, float tilt) : EntityEventArgs
{
    public float Yaw = yaw;
    public float Tilt = tilt;
}

/// <summary>Hotbar action: put the physics gun in the active hand.</summary>
public sealed partial class PhysGunSummonActionEvent : InstantActionEvent;

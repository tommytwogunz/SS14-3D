using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared.Fps3D;

/// <summary>
///     The vertical axis SS14's 2D physics doesn't have: height above the floor, vertical velocity, crouching,
///     and tipping over. Simulated by <see cref="SharedHeight3DSystem"/>; the 2D physics keeps doing X/Y.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class Height3DComponent : Component
{
    [DataField, AutoNetworkedField]
    public Height3DKind Kind = Height3DKind.Item;

    /// <summary>Height of the entity's bottom above the floor, in metres.</summary>
    [DataField, AutoNetworkedField]
    public float Z;

    /// <summary>Vertical velocity, m/s.</summary>
    [DataField, AutoNetworkedField]
    public float VZ;

    /// <summary>How tall the entity is when upright, in metres. Its top is <c>Z + Height</c>.</summary>
    [DataField, AutoNetworkedField]
    public float Height = 0.15f;

    [DataField, AutoNetworkedField]
    public bool Crouching;

    /// <summary>Tip-over angle: 0 upright, π/2 lying on its side.</summary>
    [DataField, AutoNetworkedField]
    public float Tilt;

    [DataField, AutoNetworkedField]
    public float TiltVelocity;

    /// <summary>World direction the top falls towards when tipping.</summary>
    [DataField, AutoNetworkedField]
    public Angle TiltDirection;

    /// <summary>Tumble angle while airborne (thrown items), rad.</summary>
    [DataField, AutoNetworkedField]
    public float Spin;

    /// <summary>First-person camera yaw (radians, 0 = north), enforced on the mover every tick. NaN = not first-person.</summary>
    [DataField, AutoNetworkedField]
    public float LookYaw = float.NaN;

    /// <summary>First-person camera pitch, used to aim throws (radians, up positive).</summary>
    [DataField, AutoNetworkedField]
    public float LookPitch;

    /// <summary>Held by a physics gun: the gun drives its height, no gravity.</summary>
    [DataField, AutoNetworkedField]
    public bool Held;

    /// <summary>Frozen in place by a physics gun: no gravity, body made static until grabbed again.</summary>
    [DataField, AutoNetworkedField]
    public bool Frozen;

    /// <summary>Whether the simulation should step this entity (airborne, tipping, or support may have moved).</summary>
    [ViewVariables]
    public bool Awake = true;
}

[Serializable, NetSerializable]
public enum Height3DKind : byte
{
    /// <summary>Characters: jump, crouch, step up onto low things.</summary>
    Mob,
    /// <summary>Loose small things: fall, bounce, tumble when thrown, rest on surfaces.</summary>
    Item,
    /// <summary>Loose large things (lockers, canisters, chairs): fall, can be knocked over, can be stood on.</summary>
    Prop,
    /// <summary>Tables and counters: never move vertically; a surface at their height.</summary>
    Table,
}

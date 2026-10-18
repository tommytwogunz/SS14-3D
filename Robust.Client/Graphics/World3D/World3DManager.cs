using System;
using System.Collections.Generic;
using System.Numerics;
using Robust.Shared.Maths;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Robust.Client.Graphics.World3D;

/// <summary>
///     Look direction for the first-person 3D world view (render3d.enabled).
///     Position comes from the current eye; this only holds where the camera is looking.
/// </summary>
/// <remarks>
///     World axes: X east, Y north, Z up. Yaw 0 looks north (+Y), positive yaw turns counter-clockwise
///     (towards west), matching <see cref="Robust.Shared.Maths.Angle"/>.
/// </remarks>
public interface IWorld3DManager
{
    /// <summary>Radians. 0 looks north (+Y), positive turns counter-clockwise.</summary>
    float Yaw { get; set; }

    /// <summary>Radians. Positive looks up. Clamped to just under ±90°.</summary>
    float Pitch { get; set; }

    /// <summary>Unit look vector including pitch.</summary>
    Vector3 Forward { get; }

    /// <summary>Unit look vector on the floor plane.</summary>
    Vector2 ForwardFlat { get; }

    void AddLook(float deltaYaw, float deltaPitch);

    /// <summary>
    ///     Optional hook for content: decides how an entity is drawn in the 3D view.
    ///     Return <see cref="World3DShape.Auto"/> to fall back to the engine's draw-depth heuristics.
    /// </summary>
    Func<EntityUid, World3DShape>? ShapeClassifier { get; set; }

    /// <summary>
    ///     Optional hook for content: how far open a door is, 0 (closed) to 1 (fully open).
    ///     Doors render as two halves sliding into the walls by this amount.
    /// </summary>
    Func<EntityUid, float>? DoorOpenAmount { get; set; }

    /// <summary>
    ///     Set by content while the mouse is captured for mouse-look: screen→world conversions on the 3D view then
    ///     aim through the screen centre (crosshair) instead of the cursor.
    /// </summary>
    bool CrosshairMode { get; set; }

    /// <summary>Entity under the crosshair last frame, if any.</summary>
    EntityUid? CrosshairEntity { get; }

    /// <summary>Where the crosshair ray hit last frame (entity position, or floor point).</summary>
    MapCoordinates CrosshairPoint { get; }

    /// <summary>Distance from the camera to the crosshair hit, in metres.</summary>
    float CrosshairDistance { get; }

    /// <summary>
    ///     Optional hook for content: vertical pose (height, tip-over, tumble) for entities drawn as props, mobs
    ///     and items. Null or <see cref="World3DPose.Identity"/> = standing on the floor.
    /// </summary>
    Func<EntityUid, World3DPose>? PoseProvider { get; set; }

    /// <summary>Camera height above the floor this frame (player height + eye offset). NaN = render3d.eye_height.</summary>
    float CameraHeight { get; set; }

    /// <summary>
    ///     Camera floor position this frame (world coordinates on the eye's map), e.g. the smoothed predicted player
    ///     position. Null = the eye position.
    /// </summary>
    System.Numerics.Vector2? CameraPosition { get; set; }

    /// <summary>Beams to draw this frame (physics-gun beams). Content clears and refills it every frame.</summary>
    List<World3DBeam> Beams { get; }

    /// <summary>Entity drawn as the first-person view model (the item in the active hand), if any.</summary>
    EntityUid? ViewModelEntity { get; set; }

    /// <summary>Debug: log every entity the next 3D frame draws (shape, size, prototype).</summary>
    bool DumpNextFrame { get; set; }
}

internal sealed class World3DManager : IWorld3DManager
{
    private const float MaxPitch = MathF.PI / 2f - 0.01f;

    private float _yaw;
    private float _pitch;

    public float Yaw
    {
        get => _yaw;
        set => _yaw = MathF.IEEERemainder(value, MathF.Tau);
    }

    public float Pitch
    {
        get => _pitch;
        set => _pitch = Math.Clamp(value, -MaxPitch, MaxPitch);
    }

    public Vector2 ForwardFlat => new(-MathF.Sin(_yaw), MathF.Cos(_yaw));

    public Vector3 Forward
    {
        get
        {
            var flat = ForwardFlat;
            var cosP = MathF.Cos(_pitch);
            return new Vector3(flat.X * cosP, flat.Y * cosP, MathF.Sin(_pitch));
        }
    }

    public Func<EntityUid, World3DShape>? ShapeClassifier { get; set; }

    public Func<EntityUid, float>? DoorOpenAmount { get; set; }

    public bool CrosshairMode { get; set; }
    public EntityUid? CrosshairEntity { get; internal set; }
    public MapCoordinates CrosshairPoint { get; internal set; } = MapCoordinates.Nullspace;
    public float CrosshairDistance { get; internal set; } = float.PositiveInfinity;

    public Func<EntityUid, World3DPose>? PoseProvider { get; set; }
    public float CameraHeight { get; set; } = float.NaN;
    public System.Numerics.Vector2? CameraPosition { get; set; }

    public List<World3DBeam> Beams { get; } = new();
    public EntityUid? ViewModelEntity { get; set; }

    public bool DumpNextFrame { get; set; }

    public void AddLook(float deltaYaw, float deltaPitch)
    {
        Yaw += deltaYaw;
        Pitch += deltaPitch;
    }
}

/// <summary>How an entity's sprite becomes geometry in the 3D view.</summary>
public enum World3DShape : byte
{
    /// <summary>Let the engine decide from draw depth.</summary>
    Auto,
    /// <summary>Upright flat quad that turns to face the camera (effects, overlays).</summary>
    Billboard,
    /// <summary>Lies flat on the floor (decals, pipes, puddles).</summary>
    Flat,
    /// <summary>Full-height 1×1 tile block (walls, windows, closed doors).</summary>
    Box,
    /// <summary>Not drawn.</summary>
    Hidden,
    /// <summary>Solid model extruded from the sprite's front frame, rotated with the entity (machines, lockers, chairs).</summary>
    Prop,
    /// <summary>Thick cut-out that turns to face the camera, frame picked by view angle (characters, animals).</summary>
    Mob,
    /// <summary>Thin model lying flat on the floor, or on a table on the same tile (items, corpses).</summary>
    Item,
    /// <summary>Table: the top-down sprite becomes a slab at table height on a pedestal.</summary>
    Table,
    /// <summary>Panel on the face of the nearest wall (posters, signs, alarms, APCs, wall lights).</summary>
    WallMount,
    /// <summary>Full-height thin pane on the entity's local south edge (directional windows).</summary>
    Pane,
    /// <summary>Full-height panel through the tile centre, along the wall line (doors).</summary>
    Door,
    /// <summary>Like <see cref="Door"/> but thin and see-through (grilles).</summary>
    Fence,
}

/// <summary>How an entity sits in the vertical axis, from content physics.</summary>
public struct World3DPose
{
    /// <summary>False when content has no physics height for the entity (renderer falls back to its own guesses).</summary>
    public bool Known;

    /// <summary>Height of the entity's base above the floor, metres.</summary>
    public float Z;

    /// <summary>Tip angle, radians: the top leans towards <see cref="TiltDirection"/>.</summary>
    public float Tilt;

    /// <summary>World-space unit direction the top leans towards.</summary>
    public Vector2 TiltDirection;

    public static readonly World3DPose Identity = new() { TiltDirection = new Vector2(1, 0) };
}

/// <summary>A glowing line segment in the 3D view (e.g. a physics-gun beam).</summary>
public struct World3DBeam
{
    public Vector3 Start;
    public Vector3 End;

    /// <summary>Start at the first-person view model's muzzle instead of <see cref="Start"/>.</summary>
    public bool FromViewModel;

    public Color Color;
}

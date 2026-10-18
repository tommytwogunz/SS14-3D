using System;
using System.Numerics;
using Robust.Shared.GameObjects;

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
    /// <summary>Upright quad that turns to face the camera.</summary>
    Billboard,
    /// <summary>Lies flat on the floor (decals, pipes, puddles).</summary>
    Flat,
    /// <summary>Full-height 1×1 tile block (walls, windows, closed doors).</summary>
    Box,
    /// <summary>Not drawn.</summary>
    Hidden,
}

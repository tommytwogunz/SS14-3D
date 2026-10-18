using System.Linq;
using Robust.Shared.Map;
using Content.Client.IconSmoothing;
using Content.Shared.Administration;
using Content.Shared.Doors.Components;
using Content.Shared.Fps3D;
using Content.Shared.Standing;
using System.Numerics;
using Content.Shared.Placeable;
using Content.Shared.Tag;
using Robust.Client.Graphics;
using Robust.Client.Graphics.World3D;
using Robust.Client.Input;
using Robust.Client.UserInterface;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.Console;
using Robust.Shared.Input;
using Robust.Shared.Physics.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Client.Fps3D;

/// <summary>
///     Client glue for the engine's first-person 3D view (render3d.enabled):
///     mouse-look, and telling the renderer which entities are walls, windows and doors.
/// </summary>
public sealed partial class Fps3DSystem : EntitySystem
{
    [Dependency] private IClyde _clyde = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IInputManager _input = default!;
    [Dependency] private IUserInterfaceManager _ui = default!;
    [Dependency] private IWorld3DManager _world3D = default!;
    [Dependency] private TagSystem _tags = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private Robust.Client.Player.IPlayerManager _player = default!;
    [Dependency] private SharedFps3DLookSystem _look = default!;
    [Dependency] private SharedHeight3DSystem _height = default!;
    [Dependency] private PhysGunInputSystem _physGun = default!;
    [Dependency] private Content.Shared.Hands.EntitySystems.SharedHandsSystem _hands = default!;
    [Dependency] private Robust.Shared.Containers.SharedContainerSystem _container = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private IOverlayManager _overlays = default!;
    [Dependency] private IEyeManager _eyeManager = default!;
    [Dependency] private Robust.Client.ResourceManagement.IResourceCache _cache = default!;

    private Fps3DCrosshairOverlay _crosshair = default!;

    private static readonly ProtoId<TagPrototype> WindowTag = "Window";

    /// <summary>Radians of turn per pixel of mouse movement (≈ GMod sensitivity 3 at 1x).</summary>
    public float Sensitivity = 0.0022f;

    private bool _enabled;
    private bool _mouseCaptureAllowed = true;
    private bool _mouseCaptured;

    /// <summary>Dev: aim through the crosshair even without mouse capture (fps3d_crosshairtest).</summary>
    public bool ForceCrosshair;
    private int _ignoreMotionFrames;

    // Camera interpolation between the player's positions at the last two ticks (see RecordTickPosition).
    private const float MaxCameraStep = 2f;
    private System.Numerics.Vector2 _prevTickPos;
    private System.Numerics.Vector2 _tickPos;
    private float _prevTickZ;
    private float _tickZ;
    private MapId _tickMap = MapId.Nullspace;
    private EntityUid? _lookEntity;
    private Robust.Shared.Timing.GameTick _recordedTick;
    // fps3d_camlog: per-frame camera positions for measuring smoothness.
    private double _camLogUntil;
    private readonly List<(System.Numerics.Vector2 Pos, double Time, uint Tick, float Frac, System.Numerics.Vector2 Raw)> _camLog = new();

    private float _lastSentYaw = float.NaN;
    private float _lastSentPitch = float.NaN;

    private const float EyeStanding = 1.6f;
    private const float EyeCrouching = 1.0f;
    private const float EyeLying = 0.4f;
    private float _eyeOffset = EyeStanding;
    private readonly Dictionary<EntityUid, (float Z, TimeSpan Updated)> _displayZ = new();

    // Smoothed 0..1 open amount per door, and the real time it was last updated.
    private readonly Dictionary<EntityUid, (float Amount, TimeSpan Updated)> _doorOpen = new();

    private EntityQuery<IconSmoothComponent> _smoothQuery;
    private EntityQuery<DoorComponent> _doorQuery;
    private EntityQuery<PhysicsComponent> _physicsQuery;
    private EntityQuery<PlaceableSurfaceComponent> _surfaceQuery;

    public override void Initialize()
    {
        base.Initialize();

        _smoothQuery = GetEntityQuery<IconSmoothComponent>();
        _doorQuery = GetEntityQuery<DoorComponent>();
        _physicsQuery = GetEntityQuery<PhysicsComponent>();
        _surfaceQuery = GetEntityQuery<PlaceableSurfaceComponent>();

        _world3D.ShapeClassifier = Classify;
        _world3D.DoorOpenAmount = DoorOpenAmount;
        _world3D.PoseProvider = GetPose;
        // Look events and the camera's tick samples must land before this tick's movement.
        UpdatesBefore.Add(typeof(Robust.Shared.Physics.Systems.SharedPhysicsSystem));
        _clyde.MouseMove += OnMouseMove;
        _crosshair = new Fps3DCrosshairOverlay(EntityManager, _eyeManager, _ui, _world3D, _cache);
        _overlays.AddOverlay(_crosshair);
        Subs.CVar(_cfg, CVars.Render3DEnabled, v => _enabled = _crosshair.Enabled = v, true);
        Subs.CVar(_cfg, CVars.Render3DMouseCapture, v => _mouseCaptureAllowed = v, true);
    }

    public override void Shutdown()
    {
        base.Shutdown();

        _clyde.MouseMove -= OnMouseMove;
        _overlays.RemoveOverlay(_crosshair);
        _world3D.ShapeClassifier = null;
        _world3D.DoorOpenAmount = null;
        _world3D.PoseProvider = null;
        _world3D.CameraPosition = null;
        _world3D.CameraHeight = float.NaN;
        SetMouseCaptured(false);
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        // Hold Left Alt to free the cursor for the UI (inventory, hotbar, menus), like GMod's context key.
        // Also release while typing, or while any window or popup (context menu, UIs) is open.
        var wantCapture = _enabled
                          && _mouseCaptureAllowed
                          && _clyde.MainWindow.IsFocused
                          && !_input.IsKeyDown(Keyboard.Key.Alt)
                          && _ui.KeyboardFocused == null
                          && !AnyWindowOpen();

        SetMouseCaptured(wantCapture);
        _world3D.CrosshairMode = _mouseCaptured || (_enabled && ForceCrosshair);

        if (_ignoreMotionFrames > 0)
            _ignoreMotionFrames--;

        if (_enabled)
        {
            UpdateCameraPosition();
            UpdateCameraHeight(frameTime);
            UpdateViewModelAndBeams();
        }
        else
        {
            _world3D.CameraPosition = null;
        }

        RecordCamera();
    }

    /// <summary>Starts recording the camera position every frame for <paramref name="seconds"/> (fps3d_camlog).</summary>
    private bool _camLogVerbose;

    public void StartCameraLog(float seconds, bool verbose = false)
    {
        _camLogVerbose = verbose;
        _camLog.Clear();
        _camLogUntil = _timing.RealTime.TotalSeconds + seconds;
    }

    private void RecordCamera()
    {
        if (_camLogUntil <= 0)
            return;

        var now = _timing.RealTime.TotalSeconds;
        var shown = _world3D.CameraPosition ?? _eyeManager.CurrentEye.Position.Position;
        var raw = _player.LocalEntity is { } me ? _transform.GetWorldPosition(me) : default;
        var frac = (float) (_timing.TickRemainder.TotalSeconds / _timing.TickPeriod.TotalSeconds);
        _camLog.Add((shown, now, _timing.CurTick.Value, frac, raw));
        if (now < _camLogUntil)
            return;

        _camLogUntil = 0;
        if (_camLogVerbose)
        {
            var t0 = _camLog[0].Time;
            foreach (var s in _camLog)
                Logger.GetSawmill("fps3d").Info($"camlog-frame: t={(s.Time - t0) * 1000:0} tick={s.Tick} frac={s.Frac:0.00} cam={s.Pos.X:0.000},{s.Pos.Y:0.000} raw={s.Raw.X:0.000},{s.Raw.Y:0.000}");
        }

        // Per-frame camera speed; a smooth walk has every frame near the median speed.
        var speeds = new List<float>();
        var back = 0;
        var overall = _camLog[^1].Pos - _camLog[0].Pos;
        for (var i = 1; i < _camLog.Count; i++)
        {
            var dt = (float) (_camLog[i].Time - _camLog[i - 1].Time);
            if (dt <= 0)
                continue;
            var step = _camLog[i].Pos - _camLog[i - 1].Pos;
            speeds.Add(step.Length() / dt);
            if (System.Numerics.Vector2.Dot(step, overall) < -1e-4f)
                back++;
        }

        if (speeds.Count < 3)
            return;
        var sorted = speeds.OrderBy(s => s).ToList();
        var median = sorted[sorted.Count / 2];
        var jerks = speeds.Count(s => MathF.Abs(s - median) > 0.25f * MathF.Max(median, 0.5f));
        var still = speeds.Count(s => s < 0.05f * MathF.Max(median, 0.5f));
        var rms = MathF.Sqrt(speeds.Average(s => (s - median) * (s - median))) / MathF.Max(median, 0.01f);
        var dts = new List<double>();
        for (var i = 1; i < _camLog.Count; i++)
            dts.Add((_camLog[i].Time - _camLog[i - 1].Time) * 1000);
        var fps = dts.Count / ((_camLog[^1].Time - _camLog[0].Time));
        var hitches = dts.Count(d => d > 40);
        Logger.GetSawmill("fps3d").Info($"camlog: frames={speeds.Count} fps={fps:0} maxframe={dts.Max():0}ms hitches>40ms={hitches} median={median:0.00}m/s rms={rms * 100:0}% jerks={jerks} stalled={still} backwards={back} moved={overall.Length():0.00}m");
    }

    /// <summary>Camera follows the body: eye height eases between standing, crouching and lying; Z is the predicted height.</summary>
    private void UpdateCameraHeight(float frameTime)
    {
        if (_player.LocalEntity is not { } player || !TryComp<Height3DComponent>(player, out var h))
        {
            _world3D.CameraHeight = float.NaN;
            return;
        }

        var target = !IsStanding(player) ? EyeLying : h.Crouching ? EyeCrouching : EyeStanding;
        _eyeOffset += (target - _eyeOffset) * (1f - MathF.Exp(-frameTime * 15f));
        var fraction = (float) (_timing.TickRemainder.TotalSeconds / _timing.TickPeriod.TotalSeconds);
        var z = _tickMap != MapId.Nullspace ? float.Lerp(_prevTickZ, _tickZ, Math.Clamp(fraction, 0f, 1f)) : h.Z;
        _world3D.CameraHeight = z + _eyeOffset;
    }

    private bool IsStanding(EntityUid uid) => !TryComp<StandingStateComponent>(uid, out var standing) || standing.Standing;

    private World3DPose GetPose(EntityUid uid)
    {
        if (!TryComp<Height3DComponent>(uid, out var h))
            return World3DPose.Identity;

        var pose = new World3DPose { Known = true, Z = SmoothedZ(uid, h.Z), TiltDirection = new Vector2(1, 0) };
        switch (h.Kind)
        {
            case Height3DKind.Prop when h.Tilt > 0f:
                pose.Tilt = h.Tilt;
                pose.TiltDirection = h.TiltDirection.RotateVec(new Vector2(0, -1));
                break;
            case Height3DKind.Item when h.Spin != 0f:
                pose.Tilt = h.Spin;
                break;
            case Height3DKind.Mob when !IsStanding(uid):
                // Knocked down, crit or dead: lie down, falling forwards.
                pose.Tilt = MathF.PI / 2f;
                pose.TiltDirection = Transform(uid).LocalRotation.RotateVec(new Vector2(0, -1));
                break;
        }

        return pose;
    }

    /// <summary>
    ///     Remote entities only update at the server tick rate; ease their displayed height so falls look smooth.
    ///     The local player is predicted every frame and shown as-is.
    /// </summary>
    private float SmoothedZ(EntityUid uid, float target)
    {
        if (uid == _player.LocalEntity)
            return target;

        var now = _timing.RealTime;
        if (!_displayZ.TryGetValue(uid, out var entry) || MathF.Abs(entry.Z - target) > 3f)
        {
            _displayZ[uid] = (target, now);
            return target;
        }

        var dt = (float) (now - entry.Updated).TotalSeconds;
        var z = entry.Z + (target - entry.Z) * (1f - MathF.Exp(-dt * 25f));
        _displayZ[uid] = (z, now);
        return z;
    }

    /// <summary>First-person view model (active-hand item) and physics-gun beams for this frame.</summary>
    private void UpdateViewModelAndBeams()
    {
        var local = _player.LocalEntity;
        _world3D.ViewModelEntity = local is { } p && _hands.TryGetActiveItem(p, out var item) ? item : null;

        _world3D.Beams.Clear();
        var query = EntityQueryEnumerator<PhysGunComponent, TransformComponent>();
        while (query.MoveNext(out var gunUid, out var gun, out var gunXform))
        {
            if (gun.Held is not { } held || !TryComp(held, out TransformComponent? heldXform)
                || !_container.TryGetContainingContainer((gunUid, null, null), out var container))
                continue;

            var holder = container.Owner;
            var targetPos = _transform.GetWorldPosition(heldXform);
            var targetZ = TryComp<Height3DComponent>(held, out var h) ? SmoothedZ(held, h.Z) + h.Height / 2f : 0.5f;
            var beam = new World3DBeam
            {
                End = new Vector3(targetPos, targetZ),
                Color = Color.FromHex("#6fc9ff"),
            };

            if (holder == local)
            {
                beam.FromViewModel = true;
            }
            else
            {
                var holderPos = _transform.GetWorldPosition(holder);
                var holderZ = TryComp<Height3DComponent>(holder, out var hh) ? hh.Z : 0f;
                beam.Start = new Vector3(holderPos, holderZ + 1.2f);
            }

            _world3D.Beams.Add(beam);
        }
    }

    private bool AnyWindowOpen()
    {
        foreach (var child in _ui.WindowRoot.Children)
        {
            if (child.Visible)
                return true;
        }

        foreach (var child in _ui.PopupRoot.Children)
        {
            if (child.Visible)
                return true;
        }

        return false;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Ticks only run once each for real; replays re-apply the predictive look events by themselves.
        if (!_enabled || !_timing.IsFirstTimePredicted)
            return;

        SyncLookYaw();
    }

    /// <summary>
    ///     Makes WASD follow the camera. The look direction goes out as a predictive event stamped with this tick,
    ///     so the server turns the mover at the same tick the client predicted it (a separately timed message made
    ///     the server walk along a slightly older heading, and every correction shook the camera).
    /// </summary>
    private void SyncLookYaw()
    {
        if (_player.LocalEntity is not { } player)
            return;

        if (player != _lookEntity)
        {
            _lookEntity = player;
            _lastSentYaw = float.NaN;
            _lastSentPitch = float.NaN;
        }

        var yaw = _world3D.Yaw;
        var pitch = _world3D.Pitch;
        var yawChanged = !(Math.Abs(Angle.ShortestDistance(new Angle(yaw), new Angle(_lastSentYaw)).Theta) < 0.0005);
        var pitchChanged = !(MathF.Abs(pitch - _lastSentPitch) < 0.02f);
        if (!yawChanged && !pitchChanged)
            return;

        _lastSentYaw = yaw;
        _lastSentPitch = pitch;
        RaisePredictiveEvent(new Fps3DLookMessage(yaw, pitch));
    }

    /// <summary>
    ///     The camera's own interpolation: the player's position at the start of each tick, eased between the last
    ///     two by the tick fraction (one tick behind, like Source's client interpolation). A server correction then
    ///     becomes part of one tick's motion instead of a snap, and the camera never stalls between ticks.
    /// </summary>
    public void RecordTickPosition()
    {
        if (!_enabled || !_timing.IsFirstTimePredicted || _timing.CurTick == _recordedTick)
            return;
        _recordedTick = _timing.CurTick;

        if (_player.LocalEntity is not { } player || !TryComp(player, out TransformComponent? xform))
            return;

        var pos = _transform.GetMapCoordinates(player, xform);
        if (pos.MapId != _tickMap || (pos.Position - _tickPos).LengthSquared() > MaxCameraStep * MaxCameraStep)
            _prevTickPos = pos.Position; // Teleport or map change: snap.
        else
            _prevTickPos = _tickPos;

        var z = TryComp<Height3DComponent>(player, out var h) ? h.Z : 0f;
        _prevTickZ = MathF.Abs(z - _tickZ) > MaxCameraStep ? z : _tickZ;
        _tickZ = z;

        _tickPos = pos.Position;
        _tickMap = pos.MapId;
    }

    private void UpdateCameraPosition()
    {
        if (_player.LocalEntity is not { } player
            || _tickMap == MapId.Nullspace
            || _eyeManager.CurrentEye.Position.MapId != _tickMap
            || !TryComp(player, out TransformComponent? xform)
            || xform.MapID != _tickMap)
        {
            _world3D.CameraPosition = null;
            return;
        }

        var fraction = (float) (_timing.TickRemainder.TotalSeconds / _timing.TickPeriod.TotalSeconds);
        _world3D.CameraPosition = System.Numerics.Vector2.Lerp(_prevTickPos, _tickPos, Math.Clamp(fraction, 0f, 1f));
    }

    private void SetMouseCaptured(bool captured)
    {
        if (_mouseCaptured == captured)
            return;

        _mouseCaptured = captured;
        _clyde.MainWindow.SetRelativeMouseMode(captured);
        // SDL reports one big jump when relative mode turns on (cursor warps to the window centre).
        _ignoreMotionFrames = captured ? 3 : 0;
    }

    private void OnMouseMove(MouseMoveEventArgs args)
    {
        if (!_mouseCaptured || _ignoreMotionFrames > 0)
            return;

        // Physics gun: E held with something grabbed rotates the object instead of the camera.
        if (_physGun.Rotating)
        {
            _physGun.AddRotation(args.Relative);
            return;
        }

        // Mouse right turns right (clockwise = negative yaw); mouse down looks down.
        _world3D.AddLook(-args.Relative.X * Sensitivity, -args.Relative.Y * Sensitivity);
    }

    private World3DShape Classify(EntityUid uid)
    {
        if (_doorQuery.HasComponent(uid))
            return World3DShape.Door;

        if (_smoothQuery.TryGetComponent(uid, out var smooth))
        {
            switch (smooth.Key)
            {
                case "walls" or "windows":
                    return World3DShape.Box;
                case "table":
                    return World3DShape.Table;
                case "grilles":
                    return World3DShape.Fence;
            }
        }

        // Windows without full-tile smoothing are directional panes on one tile edge.
        if (_tags.HasTag(uid, WindowTag))
            return World3DShape.Pane;

        if (_surfaceQuery.HasComponent(uid) && IsAnchored(uid))
            return World3DShape.Table;

        return World3DShape.Auto;
    }

    /// <summary>Dev override for every door's open amount (fps3d_doortest); negative = off.</summary>
    public float DoorTestAmount = -1f;

    private float DoorOpenAmount(EntityUid uid)
    {
        if (DoorTestAmount >= 0f)
            return DoorTestAmount;

        if (!_doorQuery.TryGetComponent(uid, out var door))
            return 0f;

        var opening = door.State is DoorState.Open or DoorState.Opening;
        var target = opening ? 1f : 0f;
        var now = _timing.RealTime;

        if (!_doorOpen.TryGetValue(uid, out var entry))
        {
            // First sight of this door: snap to its current state instead of animating.
            _doorOpen[uid] = (target, now);
            return target;
        }

        var duration = opening ? door.OpeningAnimationTime : door.ClosingAnimationTime;
        var step = (float) ((now - entry.Updated).TotalSeconds / Math.Max(0.05, duration.TotalSeconds));
        var amount = entry.Amount < target
            ? Math.Min(target, entry.Amount + step)
            : Math.Max(target, entry.Amount - step);

        _doorOpen[uid] = (amount, now);
        return amount;
    }

    private bool IsAnchored(EntityUid uid)
    {
        return Transform(uid).Anchored;
    }

    private bool IsSolid(EntityUid uid)
    {
        return _physicsQuery.TryGetComponent(uid, out var body) && body.CanCollide && body.Hard;
    }
}

[AnyCommand]
public sealed partial class Fps3DLookCommand : IConsoleCommand
{
    [Dependency] private IWorld3DManager _world3D = default!;

    public string Command => "fps3d_look";
    public string Description => "Set the 3D camera look direction (degrees). Yaw 0 = north, 90 = west.";
    public string Help => "Usage: fps3d_look <yaw> [pitch]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length >= 1 && float.TryParse(args[0], System.Globalization.CultureInfo.InvariantCulture, out var yaw))
            _world3D.Yaw = MathHelper.DegreesToRadians(yaw);
        if (args.Length >= 2 && float.TryParse(args[1], System.Globalization.CultureInfo.InvariantCulture, out var pitch))
            _world3D.Pitch = MathHelper.DegreesToRadians(pitch);

        shell.WriteLine($"yaw {MathHelper.RadiansToDegrees(_world3D.Yaw):0.#} pitch {MathHelper.RadiansToDegrees(_world3D.Pitch):0.#}");
    }
}

/// <summary>
///     Dev helper: teleport (via the admin <c>tp</c> command) to coordinates on the grid you are standing on,
///     e.g. positions copied from a map file.
/// </summary>
[AnyCommand]
public sealed partial class Fps3DTpGridCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private Robust.Client.Player.IPlayerManager _player = default!;

    public string Command => "fps3d_tpgrid";
    public string Description => "Teleport to grid-local coordinates on your current grid (needs admin).";
    public string Help => "Usage: fps3d_tpgrid <x> <y>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (args.Length != 2
            || !float.TryParse(args[0], inv, out var x)
            || !float.TryParse(args[1], inv, out var y)
            || _player.LocalEntity is not { } player)
        {
            shell.WriteLine(Help);
            return;
        }

        var xformSys = _entities.System<SharedTransformSystem>();

        // Use the station (the largest grid on this map), not whatever small grid we stand on: right after a round
        // starts you're on the arrivals shuttle, and map-file coordinates are station coordinates.
        var mapId = _entities.GetComponent<TransformComponent>(player).MapID;
        EntityUid? grid = null;
        var bestChunks = -1;
        var grids = _entities.EntityQueryEnumerator<Robust.Shared.Map.Components.MapGridComponent, TransformComponent>();
        while (grids.MoveNext(out var gridUid, out var gridComp, out var gridXform))
        {
            if (gridXform.MapID != mapId || gridComp.ChunkCount <= bestChunks)
                continue;
            bestChunks = gridComp.ChunkCount;
            grid = gridUid;
        }

        if (grid == null)
        {
            shell.WriteLine("No grid on this map.");
            return;
        }

        var world = System.Numerics.Vector2.Transform(new System.Numerics.Vector2(x, y), xformSys.GetWorldMatrix(grid.Value));
        shell.ExecuteCommand(string.Format(inv, "tp {0} {1}", world.X, world.Y));
    }
}

[AnyCommand]
public sealed partial class Fps3DDumpCommand : IConsoleCommand
{
    [Dependency] private IWorld3DManager _world3D = default!;

    public string Command => "fps3d_dump";
    public string Description => "Log every entity the next 3D frame draws (to the client log).";
    public string Help => "Usage: fps3d_dump";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        _world3D.DumpNextFrame = true;
        shell.WriteLine("Dumping next 3D frame to the client log.");
    }
}

[AnyCommand]
public sealed partial class Fps3DToggleCommand : IConsoleCommand
{
    [Dependency] private IConfigurationManager _cfg = default!;

    public string Command => "fps3d";
    public string Description => "Toggle the first-person 3D world view.";
    public string Help => "Usage: fps3d [on|off]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var value = args.Length switch
        {
            0 => !_cfg.GetCVar(CVars.Render3DEnabled),
            _ => args[0] is "on" or "1" or "true",
        };

        _cfg.SetCVar(CVars.Render3DEnabled, value);
        shell.WriteLine($"3D view {(value ? "on — hold Left Alt to use the mouse cursor" : "off")}");
    }
}

[AnyCommand]
public sealed partial class Fps3DDoorTestCommand : IConsoleCommand
{
    [Dependency] private IEntitySystemManager _systems = default!;

    public string Command => "fps3d_doortest";
    public string Description => "Force every door's 3D open amount (0..1); -1 returns to normal.";
    public string Help => "Usage: fps3d_doortest <amount>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 || !float.TryParse(args[0], System.Globalization.CultureInfo.InvariantCulture, out var amount))
        {
            shell.WriteLine(Help);
            return;
        }

        _systems.GetEntitySystem<Fps3DSystem>().DoorTestAmount = amount;
    }
}

/// <summary>Samples the player's position for the camera at the end of each tick, after movement and physics.</summary>
public sealed partial class Fps3DCameraTickSystem : EntitySystem
{
    [Dependency] private Fps3DSystem _fps = default!;

    public override void Initialize()
    {
        base.Initialize();
        UpdatesAfter.Add(typeof(Robust.Shared.Physics.Systems.SharedPhysicsSystem));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        _fps.RecordTickPosition();
    }
}

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
public sealed class Fps3DSystem : EntitySystem
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

    private static readonly TimeSpan LookSendInterval = TimeSpan.FromSeconds(1 / 30.0);
    private float _lastSentYaw = float.NaN;
    private float _lastSentPitch = float.NaN;

    private const float EyeStanding = 1.6f;
    private const float EyeCrouching = 1.0f;
    private const float EyeLying = 0.4f;
    private float _eyeOffset = EyeStanding;
    private readonly Dictionary<EntityUid, (float Z, TimeSpan Updated)> _displayZ = new();
    private TimeSpan _lastSentTime;

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
            SyncLookYaw();
            UpdateCameraHeight(frameTime);
            UpdateViewModelAndBeams();
        }
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
        _world3D.CameraHeight = h.Z + _eyeOffset;
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
            if (gun.Held is not { } held || !TryComp<TransformComponent>(held, out var heldXform)
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

    /// <summary>
    ///     Makes WASD follow the camera: predict locally every frame, and tell the server (rate-limited)
    ///     whenever the yaw changes.
    /// </summary>
    private void SyncLookYaw()
    {
        if (_player.LocalEntity is not { } player)
            return;

        var yaw = _world3D.Yaw;
        var pitch = _world3D.Pitch;
        _look.SetLookYaw(player, yaw);
        _height.SetLookPitch(player, pitch);

        var now = _timing.RealTime;
        var yawChanged = !(Math.Abs(Angle.ShortestDistance(new Angle(yaw), new Angle(_lastSentYaw)).Theta) < 0.002);
        var pitchChanged = !(MathF.Abs(pitch - _lastSentPitch) < 0.02f);
        if (!yawChanged && !pitchChanged || now - _lastSentTime < LookSendInterval)
            return;

        _lastSentYaw = yaw;
        _lastSentPitch = pitch;
        _lastSentTime = now;
        RaiseNetworkEvent(new Fps3DLookMessage(yaw, pitch));
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
public sealed class Fps3DLookCommand : IConsoleCommand
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
public sealed class Fps3DTpGridCommand : IConsoleCommand
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
        if (_entities.GetComponent<TransformComponent>(player).GridUid is not { } grid)
        {
            shell.WriteLine("Not on a grid.");
            return;
        }

        var world = System.Numerics.Vector2.Transform(new System.Numerics.Vector2(x, y), xformSys.GetWorldMatrix(grid));
        shell.ExecuteCommand(string.Format(inv, "tp {0} {1}", world.X, world.Y));
    }
}

[AnyCommand]
public sealed class Fps3DDumpCommand : IConsoleCommand
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
public sealed class Fps3DToggleCommand : IConsoleCommand
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
public sealed class Fps3DDoorTestCommand : IConsoleCommand
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

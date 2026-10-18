using Content.Shared.Administration;
using Content.Shared.Movement.Components;
using Robust.Client.GameObjects;
using Robust.Client.Graphics.World3D;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Shared.Console;
using Robust.Shared.Input;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Client.Fps3D;

// Dev commands for testing the first-person view through the console / dev command file,
// without driving the OS keyboard or mouse.

/// <summary>Treat the view as mouse-captured (crosshair aiming) even when the window isn't focused.</summary>
[AnyCommand]
public sealed class Fps3DCrosshairTestCommand : IConsoleCommand
{
    [Dependency] private IEntitySystemManager _systems = default!;

    public string Command => "fps3d_crosshairtest";
    public string Description => "Force crosshair aiming on (1) or back to automatic (0).";
    public string Help => "Usage: fps3d_crosshairtest <0|1>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        _systems.GetEntitySystem<Fps3DSystem>().ForceCrosshair = args.Length > 0 && args[0] is "1" or "on" or "true";
    }
}

/// <summary>
///     Sends a key function as the game would for a real key press: an input command carrying the crosshair's
///     target entity and coordinates, through the client InputSystem (predicted locally and sent to the server).
/// </summary>
[AnyCommand]
public sealed class Fps3DKeyCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IInputManager _input = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IWorld3DManager _world3D = default!;

    public string Command => "fps3d_key";
    public string Description => "Simulate a key function (e.g. MoveUp, Use, ActivateItemInWorld) at the crosshair.";
    public string Help => "Usage: fps3d_key <function> <down|up>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2)
        {
            shell.WriteLine(Help);
            return;
        }

        var func = new BoundKeyFunction(args[0]);
        if (!_input.NetworkBindMap.FunctionExists(args[0]))
        {
            shell.WriteLine($"Unknown key function {args[0]}");
            return;
        }

        var state = args[1] == "down" ? BoundKeyState.Down : BoundKeyState.Up;
        var input = _entities.System<InputSystem>();
        var maps = _entities.System<MapSystem>();
        var xforms = _entities.System<SharedTransformSystem>();

        var point = _world3D.CrosshairPoint;
        var coords = point.MapId == MapId.Nullspace
            ? EntityCoordinates.Invalid
            : maps.TryFindGridAt(point, out var grid, out _)
                ? maps.MapToGrid(grid, point)
                : xforms.ToCoordinates(point);

        var message = new ClientFullInputCmdMessage(_timing.CurTick, _timing.TickFraction,
            _input.NetworkBindMap.KeyFunctionID(func))
        {
            State = state,
            Coordinates = coords,
            ScreenCoordinates = default,
            Uid = _world3D.CrosshairEntity ?? default,
        };

        input.HandleInputCommand(_player.LocalSession, func, message);
        var target = _world3D.CrosshairEntity is { } uid ? _entities.ToPrettyString(uid).ToString() : "floor";
        shell.WriteLine($"{args[0]} {args[1]} → {target} at {point}");
        Logger.GetSawmill("fps3d").Info($"key {args[0]} {args[1]} → {target} at {point}");
    }
}

/// <summary>Logs the local player's position and movement frame.</summary>
[AnyCommand]
public sealed class Fps3DWhereCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IWorld3DManager _world3D = default!;

    public string Command => "fps3d_where";
    public string Description => "Log the local player's world position, rotation and mover frame.";
    public string Help => "Usage: fps3d_where";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (_player.LocalEntity is not { } uid)
            return;

        var xforms = _entities.System<SharedTransformSystem>();
        var (pos, rot) = xforms.GetWorldPositionRotation(uid);
        var line = $"where: pos={pos} rot={rot.Degrees:0.0} yaw={MathHelper.RadiansToDegrees(_world3D.Yaw):0.0}";
        if (_entities.TryGetComponent(uid, out InputMoverComponent? mover))
            line += $" moverRel={mover.RelativeRotation.Degrees:0.0} target={mover.TargetRelativeRotation.Degrees:0.0}";

        line += $" crosshair={(_world3D.CrosshairEntity is { } t ? _entities.ToPrettyString(t).ToString() : "none")} dist={_world3D.CrosshairDistance:0.00} mode={_world3D.CrosshairMode}";
        shell.WriteLine(line);
        Logger.GetSawmill("fps3d").Info(line);
    }
}

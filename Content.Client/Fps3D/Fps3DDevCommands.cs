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
public sealed partial class Fps3DCrosshairTestCommand : IConsoleCommand
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
public sealed partial class Fps3DKeyCommand : IConsoleCommand
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
            ScreenCoordinates = _input.MouseScreenPosition, // what a real click carries (the crosshair in 3D)
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
public sealed partial class Fps3DWhereCommand : IConsoleCommand
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

        if (_entities.TryGetComponent(uid, out Content.Shared.Fps3D.Height3DComponent? h))
            line += $" z={h.Z:0.00} vz={h.VZ:0.00} crouch={h.Crouching} cam={_world3D.CameraHeight:0.00}";
        line += $" crosshair={(_world3D.CrosshairEntity is { } t ? _entities.ToPrettyString(t).ToString() : "none")} dist={_world3D.CrosshairDistance:0.00} mode={_world3D.CrosshairMode}";
        shell.WriteLine(line);
        Logger.GetSawmill("fps3d").Info(line);
    }
}

/// <summary>Logs height state of nearby entities whose prototype ID contains the given text.</summary>
[AnyCommand]
public sealed partial class Fps3DTrackCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IPlayerManager _player = default!;

    public string Command => "fps3d_track";
    public string Description => "Log height/tilt of nearby entities whose prototype ID contains <text>.";
    public string Help => "Usage: fps3d_track <text>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 || _player.LocalEntity is not { } player)
            return;

        var xforms = _entities.System<SharedTransformSystem>();
        var me = xforms.GetWorldPosition(player);
        var heights = _entities.GetEntityQuery<Content.Shared.Fps3D.Height3DComponent>();
        var containers = _entities.System<Robust.Shared.Containers.SharedContainerSystem>();
        var query = _entities.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var meta, out var xform))
        {
            if (xform.MapUid == null || containers.IsEntityInContainer(uid))
                continue;
            heights.TryGetComponent(uid, out var h);
            var id = meta.EntityPrototype?.ID ?? "";
            if (!id.Contains(args[0], StringComparison.OrdinalIgnoreCase))
                continue;

            var pos = xforms.GetWorldPosition(uid);
            if ((pos - me).Length() > 15f)
                continue;

            var grot = xform.GridUid is { } g ? xforms.GetWorldRotation(g).Degrees : 0.0;
            var line = $"track: {id} {uid} pos={pos} d={(pos - me).Length():0.0} z={h?.Z ?? 0f:0.00} vz={h?.VZ ?? 0f:0.00} tilt={h?.Tilt ?? 0f:0.00} spin={h?.Spin ?? 0f:0.00} grot={grot:0.0}";
            shell.WriteLine(line);
            Logger.GetSawmill("fps3d").Info(line);
        }
    }
}

/// <summary>Sends the physics gun's wheel/rotate messages (the ones mouse wheel and E + mouse send) for testing.</summary>
[AnyCommand]
public sealed partial class Fps3DPhysGunCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "fps3d_physgun";
    public string Description => "Physics gun test input: scroll <delta> | rotate <yaw> <tilt> (radians).";
    public string Help => "Usage: fps3d_physgun scroll <delta> | fps3d_physgun rotate <yaw> <tilt>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        EntityEventArgs? msg = args switch
        {
            ["scroll", var d] when float.TryParse(d, inv, out var delta) => new Content.Shared.Fps3D.PhysGunScrollMessage(delta),
            ["rotate", var y, var t] when float.TryParse(y, inv, out var yaw) && float.TryParse(t, inv, out var tilt)
                => new Content.Shared.Fps3D.PhysGunRotateMessage(yaw, tilt),
            _ => null,
        };

        if (msg == null)
        {
            shell.WriteLine(Help);
            return;
        }

        _entities.EntityNetManager.SendSystemNetworkMessage(msg);
    }
}

/// <summary>Closes open windows, menus and popups (e.g. the escape menu or a context menu) for clean tests.</summary>
[AnyCommand]
public sealed partial class Fps3DCloseUiCommand : IConsoleCommand
{
    [Dependency] private Robust.Client.UserInterface.IUserInterfaceManager _ui = default!;

    public string Command => "fps3d_closeui";
    public string Description => "Close all open windows and popups.";
    public string Help => "Usage: fps3d_closeui";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        foreach (var child in System.Linq.Enumerable.ToArray(_ui.WindowRoot.Children))
        {
            if (child is Robust.Client.UserInterface.CustomControls.BaseWindow window)
                window.Close();
            else
                child.Visible = false;
        }

        foreach (var child in System.Linq.Enumerable.ToArray(_ui.PopupRoot.Children))
        {
            child.Visible = false;
        }
    }
}

/// <summary>
///     Logs the gameplay state of the crosshair target (or "self"): anchoring, damage, mob/door state, light,
///     solutions, hands, combat mode. Used by the stage 6 gameplay checks.
/// </summary>
[AnyCommand]
public sealed partial class Fps3DInspectCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IWorld3DManager _world3D = default!;

    public string Command => "fps3d_inspect";
    public string Description => "Log gameplay state of the crosshair target, or 'self'.";
    public string Help => "Usage: fps3d_inspect [self]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        EntityUid? target = args switch
        {
            ["self"] => _player.LocalEntity,
            [var id] when int.TryParse(id, out var n) => new EntityUid(n),
            _ => _world3D.CrosshairEntity,
        };
        if (target is not { } uid || !_entities.EntityExists(uid))
        {
            Logger.GetSawmill("fps3d").Info("inspect: nothing");
            return;
        }

        var e = _entities;
        var parts = new List<string> { $"inspect: {e.ToPrettyString(uid)}" };
        var xform = e.GetComponent<TransformComponent>(uid);
        parts.Add($"anchored={xform.Anchored}");

        if (e.TryGetComponent(uid, out Content.Shared.Damage.Components.DamageableComponent? dmg))
            parts.Add($"damage={e.System<Content.Shared.Damage.Systems.DamageableSystem>().GetTotalDamage((uid, dmg))}");
        if (e.TryGetComponent(uid, out Content.Shared.Mobs.Components.MobStateComponent? mob))
            parts.Add($"mob={mob.CurrentState}");
        if (e.TryGetComponent(uid, out Content.Shared.Standing.StandingStateComponent? standing))
            parts.Add($"standing={standing.Standing}");
        if (e.TryGetComponent(uid, out Content.Shared.Doors.Components.DoorComponent? door))
            parts.Add($"door={door.State}");
        if (e.TryGetComponent(uid, out Robust.Client.GameObjects.PointLightComponent? light))
            parts.Add($"light={light.Enabled}");
        if (e.TryGetComponent(uid, out Content.Shared.CombatMode.CombatModeComponent? combat))
            parts.Add($"combat={combat.IsInCombatMode}");

        var solutions = e.System<Content.Shared.Chemistry.EntitySystems.SharedSolutionContainerSystem>();
        foreach (var (name, soln) in solutions.EnumerateSolutions(uid))
            parts.Add($"solution[{name}]={soln.Comp.Solution.Volume}/{soln.Comp.Solution.MaxVolume}");

        var hands = e.System<Content.Shared.Hands.EntitySystems.SharedHandsSystem>();
        if (hands.TryGetActiveItem(uid, out var held))
            parts.Add($"active={e.ToPrettyString(held.Value)}");

        if (e.TryGetComponent(uid, out Content.Shared.Stacks.StackComponent? stack))
            parts.Add($"stack={stack.Count}");

        var line = string.Join(" ", parts);
        shell.WriteLine(line);
        Logger.GetSawmill("fps3d").Info(line);
    }
}

/// <summary>Logs which windows and popups are open (to check UIs work in first person).</summary>
[AnyCommand]
public sealed partial class Fps3DUiCommand : IConsoleCommand
{
    [Dependency] private Robust.Client.UserInterface.IUserInterfaceManager _ui = default!;

    public string Command => "fps3d_ui";
    public string Description => "Log open windows and popups.";
    public string Help => "Usage: fps3d_ui";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var open = new List<string>();
        foreach (var child in _ui.WindowRoot.Children)
        {
            if (child.Visible)
                open.Add(child is Robust.Client.UserInterface.CustomControls.DefaultWindow w ? $"{child.GetType().Name}('{w.Title}')" : child.GetType().Name);
        }

        foreach (var child in _ui.PopupRoot.Children)
        {
            if (child.Visible)
                open.Add($"popup:{child.GetType().Name}");
        }

        foreach (var child in _ui.ModalRoot.Children)
        {
            if (child.Visible)
                open.Add($"modal:{child.GetType().Name}");
        }

        var line = $"ui: [{string.Join(", ", open)}]";
        shell.WriteLine(line);
        Logger.GetSawmill("fps3d").Info(line);
    }
}

/// <summary>Triggers one of the local player's actions by prototype (like clicking it on the hotbar).</summary>
[AnyCommand]
public sealed partial class Fps3DActionCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IPlayerManager _player = default!;

    public string Command => "fps3d_action";
    public string Description => "Trigger the local player's action whose prototype ID contains <text> (e.g. CombatMode).";
    public string Help => "Usage: fps3d_action <text>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 || _player.LocalEntity is not { } player)
            return;

        var actions = _entities.System<Content.Client.Actions.ActionsSystem>();
        foreach (var action in actions.GetActions(player))
        {
            var id = _entities.GetComponent<MetaDataComponent>(action).EntityPrototype?.ID ?? "";
            if (!id.Contains(args[0], StringComparison.OrdinalIgnoreCase))
                continue;

            actions.TriggerAction(action);
            Logger.GetSawmill("fps3d").Info($"action: triggered {id}");
            return;
        }

        Logger.GetSawmill("fps3d").Info($"action: none matching {args[0]}");
    }
}

/// <summary>Logs what SS14's 2D click test (used by melee and interactions) finds at the crosshair point.</summary>
[AnyCommand]
public sealed partial class Fps3DClickTestCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IWorld3DManager _world3D = default!;
    [Dependency] private Robust.Client.State.IStateManager _state = default!;
    [Dependency] private Robust.Client.Graphics.IEyeManager _eye = default!;
    [Dependency] private Robust.Client.Input.IInputManager _input = default!;

    public string Command => "fps3d_clicktest";
    public string Description => "Log the 2D clicked entity at the crosshair point and its distance.";
    public string Help => "Usage: fps3d_clicktest";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var mouse = _eye.PixelToMap(_input.MouseScreenPosition);
        var point = _world3D.CrosshairPoint;
        EntityUid? clicked = null;
        if (_state.CurrentState is Content.Client.Gameplay.GameplayStateBase screen)
            clicked = screen.GetClickedEntity(mouse);
        var me = _player.LocalEntity is { } p ? _entities.System<SharedTransformSystem>().GetMapCoordinates(p).Position : default;
        var line = $"clicktest: mouseMap={mouse} crosshairPoint={point} crosshairEnt={(_world3D.CrosshairEntity is { } c ? _entities.ToPrettyString(c).ToString() : "none")} clicked2D={(clicked is { } k ? _entities.ToPrettyString(k).ToString() : "none")} distFromMe={(mouse.Position - me).Length():0.00}";
        shell.WriteLine(line);
        Logger.GetSawmill("fps3d").Info(line);
    }
}

/// <summary>Clicks the item in an inventory slot, as clicking it on the inventory bar does (e.g. opens the PDA).</summary>
[AnyCommand]
public sealed partial class Fps3DSlotCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IPlayerManager _player = default!;

    public string Command => "fps3d_slot";
    public string Description => "Activate the item in an inventory slot (like clicking it on the inventory bar).";
    public string Help => "Usage: fps3d_slot <slot id, e.g. id>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 || _player.LocalEntity is not { } player)
            return;

        var inventory = _entities.System<Content.Client.Inventory.ClientInventorySystem>();
        if (!inventory.TryGetSlotEntity(player, args[0], out var item))
        {
            shell.WriteLine($"Nothing in slot {args[0]}.");
            return;
        }

        inventory.UIInventoryActivateItem(args[0], player);
        Logger.GetSawmill("fps3d").Info($"slot {args[0]}: activated {_entities.ToPrettyString(item.Value)}");
    }
}

/// <summary>Records the camera every frame and logs how smooth its motion was (for checking movement jitter).</summary>
[AnyCommand]
public sealed partial class Fps3DCamLogCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "fps3d_camlog";
    public string Description => "Record camera motion for <seconds> and log per-frame smoothness stats.";
    public string Help => "Usage: fps3d_camlog <seconds>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 1 || !float.TryParse(args[0], System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            shell.WriteLine(Help);
            return;
        }

        _entities.System<Fps3DSystem>().StartCameraLog(seconds, args.Length > 1 && args[1] == "v");
    }
}

/// <summary>Lists an entity's mapped sprite layers (key, visible, RSI state) — for matching 3D model colour slots.</summary>
[AnyCommand]
public sealed partial class Fps3DLayersCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "fps3d_layers";
    public string Description => "List an entity's mapped sprite layers.";
    public string Help => "Usage: fps3d_layers <entity uid>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 || !EntityUid.TryParse(args[0], out var uid) || !_entities.TryGetComponent(uid, out SpriteComponent? sprite))
        {
            shell.WriteLine(Help);
            return;
        }

        var sprites = _entities.System<SpriteSystem>();
        var sb = new System.Text.StringBuilder();
        var keys = new List<object> { "jumpsuit", "outerClothing", "shoes", "head", "gloves", "back", "belt", "neck", "mask", "eyes", "ears", "id" };
        foreach (var e in Enum.GetValues<Content.Shared.Humanoid.HumanoidVisualLayers>())
            keys.Add(e);
        foreach (var key in keys)
        {
            var found = key is Enum en ? sprites.LayerMapTryGet((uid, sprite), en, out var index, false) : sprites.LayerMapTryGet((uid, sprite), (string) key, out index, false);
            if (!found)
                continue;
            var layer = (SpriteComponent.Layer) sprite[index];
            sb.Append($" [{key}={index}:{(layer.Visible ? "on" : "off")}:{layer.ActualState?.StateId}]");
        }

        Logger.GetSawmill("fps3d").Info($"layers: {System.Linq.Enumerable.Count(sprite.AllLayers)} total;{sb}");
    }
}

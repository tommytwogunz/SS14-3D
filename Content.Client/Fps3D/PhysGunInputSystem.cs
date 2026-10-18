using System.Numerics;
using Content.Client.ContextMenu.UI;
using Content.Shared.Fps3D;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Input;
using Content.Shared.Interaction;
using Robust.Client.Graphics;
using Robust.Client.Graphics.World3D;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Shared.Input;
using Robust.Shared.Input.Binding;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Client.Fps3D;

/// <summary>
///     Client input for the physics gun, active only while it is the item in the active hand:
///     hold left mouse to grab what the crosshair is on, release to drop/throw, right-click to freeze,
///     mouse wheel for distance, hold E and move the mouse to rotate. Everything else passes through untouched.
/// </summary>
public sealed partial class PhysGunInputSystem : EntitySystem
{
    [Dependency] private IClyde _clyde = default!;
    [Dependency] private IInputManager _input = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IWorld3DManager _world3D = default!;
    [Dependency] private SharedHandsSystem _hands = default!;

    private static readonly TimeSpan RotateSendInterval = TimeSpan.FromSeconds(1 / 30.0);
    /// <summary>Radians of object rotation per pixel of mouse movement while rotating.</summary>
    private const float RotateSensitivity = 0.006f;

    private float _pendingYaw;
    private float _pendingTilt;
    private TimeSpan _lastRotateSend;

    /// <summary>True while E is held with something grabbed: mouse movement rotates the object, not the camera.</summary>
    public bool Rotating { get; private set; }

    public override void Initialize()
    {
        base.Initialize();

        CommandBinds.Builder
            // Before drag-and-drop too: it holds back presses to detect drags and swallows the release,
            // which would turn hold-to-grab into grab-on-release and lose the release.
            .BindBefore(EngineKeyFunctions.Use, new PointerInputCmdHandler(OnUse, ignoreUp: false, outsidePrediction: true),
                typeof(SharedInteractionSystem), typeof(Content.Client.Interaction.DragDropSystem))
            .BindBefore(ContentKeyFunctions.ActivateItemInWorld, new PointerInputCmdHandler(OnActivate, ignoreUp: false, outsidePrediction: true),
                typeof(SharedInteractionSystem))
            .BindBefore(EngineKeyFunctions.UseSecondary, new PointerInputCmdHandler(OnUseSecondary, outsidePrediction: true),
                typeof(EntityMenuUIController))
            .Register<PhysGunInputSystem>();

        _clyde.MouseWheel += OnMouseWheel;
    }

    public override void Shutdown()
    {
        base.Shutdown();
        CommandBinds.Unregister<PhysGunInputSystem>();
        _clyde.MouseWheel -= OnMouseWheel;
    }

    /// <summary>The physics gun in the local player's active hand, if any.</summary>
    public bool TryGetHeldGun(out PhysGunComponent gun)
    {
        gun = default!;
        return _player.LocalEntity is { } player
               && _hands.TryGetActiveItem(player, out var item)
               && TryComp(item, out gun!);
    }

    private bool OnUse(in PointerInputCmdHandler.PointerInputCmdArgs args)
    {
        if (!TryGetHeldGun(out var gun))
            return false;

        if (args.State == BoundKeyState.Down)
        {
            // Grab what the crosshair (or cursor) is on, at the distance it currently is.
            if (args.EntityUid.IsValid() && args.EntityUid != _player.LocalEntity)
            {
                var distance = float.IsFinite(_world3D.CrosshairDistance) && _world3D.CrosshairEntity == args.EntityUid
                    ? _world3D.CrosshairDistance
                    : 2f;
                RaiseNetworkEvent(new PhysGunGrabMessage(GetNetEntity(args.EntityUid), distance));
            }
        }
        else if (gun.Held != null)
        {
            RaiseNetworkEvent(new PhysGunReleaseMessage());
        }

        return true; // The physics gun owns left mouse while it's out.
    }

    private bool OnActivate(in PointerInputCmdHandler.PointerInputCmdArgs args)
    {
        // E is the rotate key while something is held (GMod's +use); otherwise E works as usual.
        return TryGetHeldGun(out var gun) && gun.Held != null;
    }

    private bool OnUseSecondary(in PointerInputCmdHandler.PointerInputCmdArgs args)
    {
        if (!TryGetHeldGun(out var gun) || gun.Held == null)
            return false; // Normal right-click (context menu).

        RaiseNetworkEvent(new PhysGunFreezeMessage());
        return true;
    }

    private void OnMouseWheel(MouseWheelEventArgs args)
    {
        if (!TryGetHeldGun(out var gun) || gun.Held == null || args.Delta.Y == 0)
            return;

        RaiseNetworkEvent(new PhysGunScrollMessage(args.Delta.Y));
    }

    /// <summary>Called by <see cref="Fps3DSystem"/> with captured mouse movement while <see cref="Rotating"/>.</summary>
    public void AddRotation(Vector2 mouseDelta)
    {
        // Mouse right spins clockwise (as seen from above), mouse up tips the top away.
        _pendingYaw -= mouseDelta.X * RotateSensitivity;
        _pendingTilt -= mouseDelta.Y * RotateSensitivity;
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        Rotating = TryGetHeldGun(out var gun) && gun.Held != null && _input.IsKeyDown(Keyboard.Key.E);

        if (_pendingYaw == 0f && _pendingTilt == 0f || _timing.RealTime - _lastRotateSend < RotateSendInterval)
            return;

        if (Rotating)
            RaiseNetworkEvent(new PhysGunRotateMessage(_pendingYaw, _pendingTilt));

        _pendingYaw = 0f;
        _pendingTilt = 0f;
        _lastRotateSend = _timing.RealTime;
    }
}

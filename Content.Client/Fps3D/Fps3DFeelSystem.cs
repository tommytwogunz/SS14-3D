using System.Numerics;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Fps3D;
using Content.Shared.Standing;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Client.Graphics.World3D;
using Robust.Client.Player;
using Robust.Shared.Physics.Components;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Client.Fps3D;

/// <summary>
///     First-person game feel, GMod/Source style, layered on SS14's own combat and movement:
///     your arms on screen holding what's in your hand (bob while walking, sway with the mouse, a punch or swing when
///     you attack, a kick when you shoot), hitmarkers when an attack lands, a view punch when you get hurt, and the
///     camera rolling over when you're knocked down or slip.
///     Everything here is client-only presentation: what happens in the game is still decided by SS14's systems.
/// </summary>
public sealed partial class Fps3DFeelSystem : EntitySystem
{
    [Dependency] private IWorld3DManager _world3D = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private Fps3DSystem _fps = default!;
    [Dependency] private Fps3DModelSystem _models = default!;
    [Dependency] private SharedMeleeWeaponSystem _melee = default!;
    [Dependency] private SharedGunSystem _gun = default!;
    [Dependency] private DamageableSystem _damageable = default!;

    private const float SwingTime = 0.4f;
    private const float HitWindow = 0.6f;
    private bool _debugLog = false;

    private readonly World3DModelRequest _arms = new() { Model = "viewarms" };

    // Attack tracking.
    private EntityUid? _meleeWeapon;
    private TimeSpan _lastNextAttack;
    private EntityUid? _gunWeapon;
    private int _lastShots;
    private float _swing = -1f; // seconds since the swing started, < 0 = none
    private bool _swingArmed;
    private float _recoil;      // 0..1, decays

    // Hitmarkers: an attack's target and its damage when we attacked.
    private EntityUid? _hitTarget;
    private float _hitTargetDamage;
    private float _hitWindow;

    // Camera punch: a damped spring back to zero (Source's view punch), plus the knocked-down roll.
    private Vector3 _punch;
    private Vector3 _punchVel;
    private float _roll;
    private float _lastOwnDamage = -1f;

    // View model motion.
    private float _bobPhase;
    private float _bobAmount;
    private Vector2 _sway;
    private float _lastYaw = float.NaN;
    private float _lastPitch = float.NaN;
    private float _land;
    private bool _wasAirborne;

    public override void Initialize()
    {
        base.Initialize();
        UpdatesAfter.Add(typeof(Fps3DSystem));
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _world3D.ViewArms = null;
        _world3D.CameraPunch = Vector3.Zero;
        _world3D.ViewModelOffset = Matrix4x4.Identity;
        _world3D.DrawLocalBody = false;
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);
        var dt = Math.Clamp(frameTime, 0f, 0.1f);
        _fps.Crosshair.Now = _timing.RealTime;

        if (_player.LocalEntity is not { } player || !HasComp<Height3DComponent>(player))
        {
            _world3D.ViewArms = null;
            _world3D.CameraPunch = Vector3.Zero;
            _world3D.DrawLocalBody = false;
            return;
        }

        var standing = !TryComp(player, out StandingStateComponent? stand) || stand.Standing;
        _world3D.DrawLocalBody = true;

        DetectAttacks(player);
        UpdateHitmarker(dt);
        UpdateCameraPunch(player, standing, dt);
        UpdateViewModel(player, standing, dt);
    }

    /// <summary>SS14's melee and guns move their cooldown/shot counters when you attack; that's our cue to animate.</summary>
    private void DetectAttacks(EntityUid player)
    {
        if (_melee.TryGetWeapon(player, out var weapon, out var melee))
        {
            if (_debugLog && (weapon != _meleeWeapon || melee.NextAttack != _lastNextAttack))
                Log.Info($"melee weapon {ToPrettyString(weapon)} next {melee.NextAttack} (was {_lastNextAttack})");
            if (weapon == _meleeWeapon && melee.NextAttack > _lastNextAttack)
            {
                _swing = 0f;
                _swingArmed = weapon != player;
                Log.Debug($"swing with {ToPrettyString(weapon)}");
                ArmHitmarker();
                Punch(new Vector3(-0.012f, _swingArmed ? 0.02f : 0.008f, 0f));
            }

            _meleeWeapon = weapon;
            _lastNextAttack = melee.NextAttack;
        }

        if (_gun.TryGetGun(player, out var gun))
        {
            if (gun.Owner == _gunWeapon && gun.Comp.ShotCounter != _lastShots && gun.Comp.ShotCounter > 0)
            {
                // Source-style recoil: kick the view up (it springs back) and the gun back into the hand.
                _recoil = 1f;
                Punch(new Vector3(0.035f + _random.NextFloat(0f, 0.015f), _random.NextFloat(-0.012f, 0.012f), 0f));
                ArmHitmarker();
            }

            _gunWeapon = gun.Owner;
            _lastShots = gun.Comp.ShotCounter;
        }
        else
        {
            _gunWeapon = null;
        }
    }

    private void ArmHitmarker()
    {
        _hitTarget = _world3D.CrosshairEntity;
        _hitTargetDamage = _hitTarget is { } t && TryComp(t, out DamageableComponent? dmg)
            ? (float) _damageable.GetTotalDamage((t, dmg))
            : 0f;
        _hitWindow = HitWindow;
    }

    private void UpdateHitmarker(float dt)
    {
        if (_hitWindow <= 0f || _hitTarget is not { } target)
            return;

        _hitWindow -= dt;
        if (!TryComp(target, out DamageableComponent? dmg))
            return;

        var now = (float) _damageable.GetTotalDamage((target, dmg));
        if (now > _hitTargetDamage + 0.01f)
        {
            _fps.Crosshair.HitMarkerUntil = _timing.RealTime + TimeSpan.FromSeconds(0.25);
            _hitWindow = 0f;
        }
    }

    private void Punch(Vector3 kick) => _punchVel += kick * 18f;

    private void UpdateCameraPunch(EntityUid player, bool standing, float dt)
    {
        // Getting hurt kicks the view, harder for bigger hits.
        if (TryComp(player, out DamageableComponent? dmg))
        {
            var total = (float) _damageable.GetTotalDamage((player, dmg));
            if (_lastOwnDamage >= 0f && total > _lastOwnDamage + 0.5f)
            {
                var k = Math.Clamp((total - _lastOwnDamage) / 15f, 0.3f, 1.5f);
                Punch(new Vector3(_random.NextFloat(0.02f, 0.05f) * k, _random.NextFloat(-0.04f, 0.04f) * k, _random.NextFloat(-0.03f, 0.03f) * k));
            }

            _lastOwnDamage = total;
        }

        // Damped spring back to zero.
        _punchVel += (-_punch * 220f - _punchVel * 24f) * dt;
        _punch += _punchVel * dt;

        // Knocked down or slipped: the view rolls over as you hit the floor, and rights itself when you get up.
        var rollTarget = standing ? 0f : 0.42f;
        _roll += (rollTarget - _roll) * (1f - MathF.Exp(-dt * (standing ? 6f : 9f)));

        _world3D.CameraPunch = _punch + new Vector3(0, 0, _roll);
    }

    private void UpdateViewModel(EntityUid player, bool standing, float dt)
    {
        // No arms while down (your view is on the floor) or for non-humanoids.
        if (!standing || !TryComp(player, out Robust.Client.GameObjects.SpriteComponent? _))
        {
            _world3D.ViewArms = null;
            return;
        }

        _models.FillArmColors(player, _arms);
        _world3D.ViewArms = _arms;

        // Walk bob: phase advances with speed, amount eases with it.
        var speed = TryComp(player, out PhysicsComponent? body) ? body.LinearVelocity.Length() : 0f;
        var airborne = TryComp(player, out Height3DComponent? h) && h.Z > 0.05f;
        _bobAmount += ((airborne ? 0f : Math.Clamp(speed / 4.5f, 0f, 1f)) - _bobAmount) * (1f - MathF.Exp(-dt * 8f));
        _bobPhase += speed * dt * 2.2f;
        var bob = new Vector3(MathF.Sin(_bobPhase) * 0.012f, 0f, -MathF.Abs(MathF.Cos(_bobPhase)) * 0.014f) * _bobAmount;

        // Landing: the arms dip and spring back.
        if (_wasAirborne && !airborne)
            _land = 1f;
        _wasAirborne = airborne;
        _land = MathF.Max(0f, _land - dt * 4f);
        var landDip = -0.05f * MathF.Sin(_land * MathF.PI);

        // Sway: the arms lag behind mouse turns a little.
        var yaw = _world3D.Yaw;
        var pitch = _world3D.Pitch;
        if (float.IsFinite(_lastYaw) && dt > 0f)
        {
            var dYaw = Angle.ShortestDistance(new Angle(_lastYaw), new Angle(yaw)).Theta;
            var dPitch = pitch - _lastPitch;
            var target = new Vector2((float) Math.Clamp(dYaw / dt, -6, 6) * 0.006f, Math.Clamp(dPitch / dt, -6f, 6f) * 0.005f);
            _sway += (target - _sway) * (1f - MathF.Exp(-dt * 10f));
        }

        _lastYaw = yaw;
        _lastPitch = pitch;

        // Attack animation on the right arm.
        var armR = Vector3.Zero;
        var reach = 0f;
        if (_swing >= 0f)
        {
            _swing += dt;
            var t = Math.Clamp(_swing / SwingTime, 0f, 1f);
            // Fast out (first 30%), slower recovery.
            var arc = t < 0.3f ? MathF.Sin(t / 0.3f * MathF.PI / 2f) : MathF.Cos((t - 0.3f) / 0.7f * MathF.PI / 2f);
            if (_swingArmed)
            {
                // Weapon: a sweeping arc from the right across to the left, coming down.
                var sweep = MathF.Sin(t * MathF.PI * 0.5f);
                armR = new Vector3(0.35f * arc - 0.25f * sweep, 0f, 0.6f * sweep - 0.15f);
                reach = 0.06f * arc;
            }
            else
            {
                // Fist: a jab that drives the fist in towards the crosshair.
                armR = new Vector3(0.32f * arc, 0f, 0.42f * arc);
                reach = 0.08f * arc;
            }

            if (t >= 1f)
                _swing = -1f;
        }

        // Recoil: the gun kicks back and up, then settles.
        _recoil = MathF.Max(0f, _recoil - dt * 7f);
        var kick = _recoil * _recoil;
        armR += new Vector3(0.18f * kick, 0f, 0f);

        _arms.Bones["armR"] = armR;
        _arms.Bones["armL"] = Vector3.Zero;

        var offset = new Vector3(-_sway.X + bob.X, reach - 0.05f * kick, _sway.Y + bob.Z + landDip);
        _world3D.ViewModelOffset = Matrix4x4.CreateTranslation(offset);
    }
}

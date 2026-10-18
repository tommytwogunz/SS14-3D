using System;
using System.Collections.Generic;
using System.Numerics;
using Robust.Client.GameObjects;
using Robust.Client.Graphics.World3D;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Robust.Client.Graphics.Clyde
{
    // Stage 3: crosshair picking for the 3D view.
    // Every frame the emitters record an oriented box per drawn entity. Screen→world conversions on the 3D
    // viewport (Viewport.LocalToWorld) then cast a ray against those boxes and the floor, so every game system
    // that aims with the mouse (clicks, E-use, melee, guns, throwing, context menus) aims through the crosshair.
    internal partial class Clyde
    {
        private const float PickMaxDistance = 64f;

        private struct PickVolume
        {
            public EntityUid Uid;
            public Vector2 Center;
            public Vector2 HalfX;
            public Vector2 HalfY;
            public float Z0;
            public float Z1;
        }

        // Built during a frame, then swapped in so input between frames always sees a complete set.
        private List<PickVolume> _world3DPickBuild = new();
        private List<PickVolume> _world3DPickVolumes = new();

        private bool _world3DPickValid;
        private Matrix4x4 _world3DViewProj;
        private Matrix4x4 _world3DInvViewProj;
        private Vector3 _world3DCamPos;
        private Vector2i _world3DViewSize;
        private MapId _world3DMap;

        private void AddPick(EntityUid uid, Vector2 center, Vector2 halfX, Vector2 halfY, float z0, float z1)
        {
            _world3DPickBuild.Add(new PickVolume { Uid = uid, Center = center, HalfX = halfX, HalfY = halfY, Z0 = z0, Z1 = z1 });
        }

        /// <summary>Approximate clickable volume for an entity, matching how its shape is drawn.</summary>
        private void AddPickForShape(in SpriteData entry, World3DShape shape)
        {
            var rot = entry.WorldRot;
            var gridRot = GridRotation(entry);
            switch (shape)
            {
                case World3DShape.Box:
                    AddPick(entry.Uid, entry.WorldPos, rot.RotateVec(new Vector2(0.5f, 0)), rot.RotateVec(new Vector2(0, 0.5f)),
                        0f, _render3DWallHeight);
                    break;
                case World3DShape.Door:
                    if ((_world3D!.DoorOpenAmount?.Invoke(entry.Uid) ?? 0f) > 0.5f)
                        break; // Mostly open: click through to what's behind.
                    var (along, across) = PanelAxes(entry, 0.12f);
                    AddPick(entry.Uid, TileCenterWorld(entry), along, across, 0f, _render3DWallHeight);
                    break;
                case World3DShape.Fence:
                    var (fAlong, fAcross) = PanelAxes(entry, 0.05f);
                    AddPick(entry.Uid, TileCenterWorld(entry), fAlong, fAcross, 0f, _render3DWallHeight);
                    break;
                case World3DShape.Pane:
                    AddPick(entry.Uid, entry.WorldPos + rot.RotateVec(new Vector2(0, -0.39f)),
                        rot.RotateVec(new Vector2(0.5f, 0)), rot.RotateVec(new Vector2(0, 0.11f)), 0f, _render3DWallHeight);
                    break;
                case World3DShape.Table:
                    AddPick(entry.Uid, entry.WorldPos, gridRot.RotateVec(new Vector2(0.5f, 0)), gridRot.RotateVec(new Vector2(0, 0.5f)),
                        0f, TableHeight);
                    break;
                case World3DShape.Prop:
                case World3DShape.Mob:
                {
                    var half = shape == World3DShape.Mob ? 0.3f : 0.38f;
                    var height = FirstFrameHeight(entry.Sprite) * entry.Sprite.Scale.Y * _render3DUprightScale;
                    AddPick(entry.Uid, entry.WorldPos, rot.RotateVec(new Vector2(half, 0)), rot.RotateVec(new Vector2(0, half)),
                        PoseZ(entry.Uid), PoseZ(entry.Uid) + MathF.Max(0.3f, height));
                    break;
                }
                case World3DShape.Item:
                {
                    var itemPose = _world3D!.PoseProvider?.Invoke(entry.Uid) ?? World3DPose.Identity;
                    var surface = itemPose.Known ? itemPose.Z : _world3DTableTiles.Contains(TileKey(entry)) ? TableHeight : 0f;
                    var size = FirstFrameSize(entry.Sprite) * entry.Sprite.Scale / 2f;
                    // Items are small; give them a generous minimum so they're easy to grab, like GMod props.
                    AddPick(entry.Uid, entry.WorldPos, new Vector2(MathF.Max(0.18f, size.X), 0), new Vector2(0, MathF.Max(0.18f, size.Y)),
                        surface, surface + 0.15f);
                    break;
                }
            }
        }

        private float FirstFrameHeight(SpriteComponent sprite) => FirstFrameSize(sprite).Y;

        private Vector2 FirstFrameSize(SpriteComponent sprite)
        {
            foreach (var layer in sprite.Layers)
            {
                if (FrontFrame(sprite, layer) is { } frame)
                    return new Vector2(frame.Width, frame.Height) / PixelsPerMeter3D;
            }

            return new Vector2(0.5f, 0.5f);
        }

        /// <summary>Called at the end of a 3D frame: publish this frame's camera and pick volumes.</summary>
        private void FinishWorld3DPicking(in Matrix4x4 viewProj, Vector3 camPos, Vector2i size, MapId map)
        {
            (_world3DPickVolumes, _world3DPickBuild) = (_world3DPickBuild, _world3DPickVolumes);
            _world3DPickBuild.Clear();

            _world3DViewProj = viewProj;
            Matrix4x4.Invert(viewProj, out _world3DInvViewProj);
            _world3DCamPos = camPos;
            _world3DViewSize = size;
            _world3DMap = map;
            _world3DPickValid = true;

            // Publish what the crosshair is on for HUD / content.
            var mgr = (World3DManager) _world3D!;
            if (PickWorld3D(new Vector2(size.X / 2f, size.Y / 2f), out var uid, out var point, out var dist))
            {
                mgr.CrosshairEntity = uid;
                mgr.CrosshairPoint = new MapCoordinates(point, map);
                mgr.CrosshairDistance = dist;
            }
            else
            {
                mgr.CrosshairEntity = null;
                mgr.CrosshairPoint = MapCoordinates.Nullspace;
                mgr.CrosshairDistance = float.PositiveInfinity;
            }
        }

        /// <summary>
        ///     Casts a ray through a viewport pixel (render-target pixels, y down) into last frame's scene.
        ///     <paramref name="point"/> is the clicked entity's position for entity hits (what 2D click tests expect),
        ///     else where the ray meets the floor.
        /// </summary>
        private bool PickWorld3D(Vector2 pixel, out EntityUid? uid, out Vector2 point, out float distance)
        {
            uid = null;
            point = default;
            distance = float.PositiveInfinity;
            if (!_world3DPickValid)
                return false;

            var ndc = new Vector2(2f * pixel.X / _world3DViewSize.X - 1f, 1f - 2f * pixel.Y / _world3DViewSize.Y);
            var near = Vector4.Transform(new Vector4(ndc, 0f, 1f), _world3DInvViewProj);
            var far = Vector4.Transform(new Vector4(ndc, 1f, 1f), _world3DInvViewProj);
            var origin = _world3DCamPos;
            var dir = Vector3.Normalize(new Vector3(far.X, far.Y, far.Z) / far.W - new Vector3(near.X, near.Y, near.Z) / near.W);

            var best = PickMaxDistance;
            Vector2 bestPoint = default;
            EntityUid? bestUid = null;

            foreach (var vol in _world3DPickVolumes)
            {
                if (RayBox(origin, dir, vol, out var t) && t < best)
                {
                    best = t;
                    bestUid = vol.Uid;
                    bestPoint = vol.Center;
                }
            }

            // Floor.
            if (dir.Z < -1e-4f)
            {
                var tFloor = -origin.Z / dir.Z;
                if (tFloor < best)
                {
                    best = tFloor;
                    bestUid = null;
                    var hit = origin + dir * tFloor;
                    bestPoint = new Vector2(hit.X, hit.Y);
                }
            }

            if (bestUid == null && best >= PickMaxDistance)
            {
                // Looking up or into the void: aim at a point far along the view, on the floor plane.
                var hit = origin + dir * PickMaxDistance;
                bestPoint = new Vector2(hit.X, hit.Y);
            }

            uid = bestUid;
            point = bestPoint;
            distance = best;
            return true;
        }

        private static bool RayBox(Vector3 origin, Vector3 dir, in PickVolume vol, out float t)
        {
            // Slab test in the box's local frame (X along HalfX, Y along HalfY, Z up).
            t = 0;
            var ex = vol.HalfX.Length();
            var ey = vol.HalfY.Length();
            if (ex < 1e-5f || ey < 1e-5f)
                return false;

            var ax = vol.HalfX / ex;
            var ay = vol.HalfY / ey;
            var rel = new Vector2(origin.X, origin.Y) - vol.Center;
            var dirXY = new Vector2(dir.X, dir.Y);

            // Only boxes the ray enters in front of the camera count: a box around the eye (standing inside a
            // plant or a crate) is ignored, like GMod ignores what you're inside.
            var tMin = float.NegativeInfinity;
            var tMax = float.PositiveInfinity;
            return Slab(Vector2.Dot(rel, ax), Vector2.Dot(dirXY, ax), -ex, ex, ref tMin, ref tMax)
                   && Slab(Vector2.Dot(rel, ay), Vector2.Dot(dirXY, ay), -ey, ey, ref tMin, ref tMax)
                   && Slab(origin.Z, dir.Z, vol.Z0, vol.Z1, ref tMin, ref tMax)
                   && (t = tMin) > 0f;
        }

        private static bool Slab(float o, float d, float lo, float hi, ref float tMin, ref float tMax)
        {
            if (MathF.Abs(d) < 1e-7f)
                return o >= lo && o <= hi;

            var t1 = (lo - o) / d;
            var t2 = (hi - o) / d;
            if (t1 > t2)
                (t1, t2) = (t2, t1);

            tMin = MathF.Max(tMin, t1);
            tMax = MathF.Min(tMax, t2);
            return tMin <= tMax;
        }

        /// <summary>3D replacement for <see cref="Viewport.LocalToWorld"/> on the first-person viewport.</summary>
        private bool TryWorld3DLocalToWorld(Viewport viewport, Vector2 point, out MapCoordinates coords)
        {
            coords = default;
            if (!ShouldRenderWorld3D(viewport) || !_world3DPickValid)
                return false;

            // With the mouse captured the cursor position is meaningless: aim through the crosshair.
            if (_world3D!.CrosshairMode)
                point = new Vector2(_world3DViewSize.X / 2f, _world3DViewSize.Y / 2f);

            if (!PickWorld3D(point, out _, out var hit, out _))
                return false;

            coords = new MapCoordinates(hit, _world3DMap);
            return true;
        }

        /// <summary>
        ///     3D replacement for <see cref="Viewport.WorldToLocal"/>: projects a floor position (raised to chest
        ///     height) through the 3D camera, so speech bubbles and popups follow entities. Behind the camera → far off-screen.
        /// </summary>
        private bool TryWorld3DWorldToLocal(Viewport viewport, Vector2 world, out Vector2 local)
        {
            local = default;
            if (!ShouldRenderWorld3D(viewport) || !_world3DPickValid)
                return false;

            var clip = Vector4.Transform(new Vector4(world, 1.2f, 1f), _world3DViewProj);
            if (clip.W <= 0.05f)
            {
                local = new Vector2(-100000, -100000);
                return true;
            }

            var ndc = new Vector2(clip.X, clip.Y) / clip.W;
            local = new Vector2((ndc.X + 1f) / 2f * _world3DViewSize.X, (1f - ndc.Y) / 2f * _world3DViewSize.Y);
            return true;
        }
    }
}

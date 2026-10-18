using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using OpenToolkit.Graphics.OpenGL4;
using Robust.Client.GameObjects;
using Robust.Client.Graphics.World3D;
using Robust.Shared.GameObjects;
using Robust.Shared.Graphics.RSI;
using Robust.Shared;
using Robust.Shared.Maths;

namespace Robust.Client.Graphics.Clyde
{
    // Stage 2 of the 3D view: lightweight 3D models generated from the game's own sprites.
    // Every opaque pixel of a sprite frame is extruded to a given depth; side faces take the colour of the
    // edge pixel they belong to. Meshes are built once per (frame, depth), kept in one shared vertex buffer,
    // and drawn per entity with a model matrix. Category-specific placement (tables, wall mounts, doors...)
    // decides depth, orientation and position.
    internal partial class Clyde
    {
        private const float TableHeight = 0.9f;
        private const float AlphaSolid = 128;

        private float _render3DUprightScale = 1.75f;

        // CPU copies of texture alpha (downloaded once per texture, usually the sprite atlas).
        private readonly Dictionary<ClydeHandle, (byte[] Alpha, int W, int H)> _world3DAlpha = new();

        // Extruded meshes, appended into one VBO.
        private readonly Dictionary<World3DModelKey, (int Start, int Count)> _world3DModels = new();
        private readonly List<Vertex3D> _world3DModelVerts = new();
        private int _world3DModelUploaded;
        private int _world3DModelCapacity;
        private uint _world3DModelVao;
        private GLBuffer? _world3DModelVbo;

        private readonly List<World3DInstance> _world3DInstances = new();

        // Per-frame tile lookups, keyed by (grid, grid-local tile).
        private readonly HashSet<(EntityUid, Vector2i)> _world3DWallTiles = new();
        private readonly HashSet<(EntityUid, Vector2i)> _world3DTableTiles = new();
        private World3DShape[] _world3DShapes = new World3DShape[1024];

        private static readonly Vector2i[] CardinalDirs = { new(0, -1), new(0, 1), new(-1, 0), new(1, 0) };

        private readonly record struct World3DModelKey(ClydeHandle Texture, int Left, int Top, int Right, int Bottom, int DepthMm);

        private struct World3DInstance
        {
            public ClydeHandle Texture;
            public int Start;
            public int Count;
            public Matrix4x4 Model;
            public Vector4 Tint;
        }

        private void InitWorld3DModels()
        {
            _cfg.OnValueChanged(CVars.Render3DUprightScale, v => _render3DUprightScale = v, true);
        }

        #region Mesh generation

        private (byte[] Alpha, int W, int H) GetTextureAlpha(ClydeTexture texture)
        {
            if (_world3DAlpha.TryGetValue(texture.TextureId, out var cached))
                return cached;

            var loaded = _loadedTextures[texture.TextureId];
            var w = loaded.Width;
            var h = loaded.Height;
            var rgba = new byte[w * h * 4];
            var prev = GL.GetInteger(GetPName.TextureBinding2D);
            GL.BindTexture(TextureTarget.Texture2D, loaded.OpenGLObject.Handle);
            unsafe
            {
                fixed (byte* p = rgba)
                {
                    GL.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.UnsignedByte, (IntPtr) p);
                }
            }
            GL.BindTexture(TextureTarget.Texture2D, prev);
            CheckGlError();

            // GL rows are bottom-up; store alpha top-down so (x, y) matches image/atlas pixel coordinates.
            var alpha = new byte[w * h];
            for (var y = 0; y < h; y++)
            {
                var src = (h - 1 - y) * w * 4;
                var dst = y * w;
                for (var x = 0; x < w; x++)
                {
                    alpha[dst + x] = rgba[src + x * 4 + 3];
                }
            }

            cached = (alpha, w, h);
            _world3DAlpha[texture.TextureId] = cached;
            return cached;
        }

        /// <summary>
        ///     Mesh for a sprite frame extruded <paramref name="depth"/> meters along Y.
        ///     Local space: X right, front face at -Y (facing -Y), Z up from 0 to the frame height. 32 px = 1 m.
        /// </summary>
        private (ClydeHandle Texture, int Start, int Count, float Height) GetExtrudedModel(Texture frame, float depth)
        {
            var ct = RenderHandle.ExtractTexture(frame, null, out var sr);
            var left = (int) MathF.Round(sr.Left);
            var top = (int) MathF.Round(sr.Top);
            var right = (int) MathF.Round(sr.Right);
            var bottom = (int) MathF.Round(sr.Bottom);
            var height = (bottom - top) / PixelsPerMeter3D;
            var key = new World3DModelKey(ct.TextureId, left, top, right, bottom, (int) MathF.Round(depth * 1000));

            if (_world3DModels.TryGetValue(key, out var existing))
                return (ct.TextureId, existing.Start, existing.Count, height);

            var (alpha, texW, texH) = GetTextureAlpha(ct);
            var start = _world3DModelVerts.Count;
            BuildExtrusion(_world3DModelVerts, alpha, texW, texH, left, top, right, bottom, depth);
            var count = _world3DModelVerts.Count - start;
            _world3DModels[key] = (start, count);
            return (ct.TextureId, start, count, height);
        }

        private static void BuildExtrusion(List<Vertex3D> verts, byte[] alpha, int texW, int texH,
            int left, int top, int right, int bottom, float depth)
        {
            var w = right - left;
            var white = Vector4.One;
            var y0 = -depth / 2f;
            var y1 = depth / 2f;

            float X(int px) => (px - left - w / 2f) / PixelsPerMeter3D;
            float Z(int py) => (bottom - py) / PixelsPerMeter3D; // Top edge of pixel row py.
            float U(float px) => px / texW;
            float V(float py) => (texH - py) / texH;
            bool Solid(int px, int py) =>
                px >= left && px < right && py >= top && py < bottom && alpha[py * texW + px] >= AlphaSolid;

            // Front and back: one quad each, alpha-tested in the shader.
            AddQuad(verts,
                new Vector3(X(left), y0, Z(bottom)), new Vector3(X(right), y0, Z(bottom)),
                new Vector3(X(right), y0, Z(top)), new Vector3(X(left), y0, Z(top)),
                new Vector2(U(left), V(bottom)), new Vector2(U(right), V(bottom)),
                new Vector2(U(right), V(top)), new Vector2(U(left), V(top)),
                -Vector3.UnitY, white);
            AddQuad(verts,
                new Vector3(X(right), y1, Z(bottom)), new Vector3(X(left), y1, Z(bottom)),
                new Vector3(X(left), y1, Z(top)), new Vector3(X(right), y1, Z(top)),
                new Vector2(U(right), V(bottom)), new Vector2(U(left), V(bottom)),
                new Vector2(U(left), V(top)), new Vector2(U(right), V(top)),
                Vector3.UnitY, white);

            // Top and bottom edges: horizontal runs of exposed pixels. U spans the run, V sits mid-pixel.
            for (var py = top; py < bottom; py++)
            {
                for (var side = 0; side < 2; side++)
                {
                    var neighbour = side == 0 ? py - 1 : py + 1;
                    var z = side == 0 ? Z(py) : Z(py + 1);
                    var n = side == 0 ? Vector3.UnitZ : -Vector3.UnitZ;
                    var v = V(py + 0.5f);
                    var px = left;
                    while (px < right)
                    {
                        if (!Solid(px, py) || Solid(px, neighbour))
                        {
                            px++;
                            continue;
                        }

                        var runStart = px;
                        while (px < right && Solid(px, py) && !Solid(px, neighbour))
                            px++;

                        AddQuad(verts,
                            new Vector3(X(runStart), y0, z), new Vector3(X(px), y0, z),
                            new Vector3(X(px), y1, z), new Vector3(X(runStart), y1, z),
                            new Vector2(U(runStart + 0.5f), v), new Vector2(U(px - 0.5f), v),
                            new Vector2(U(px - 0.5f), v), new Vector2(U(runStart + 0.5f), v),
                            n, white);
                    }
                }
            }

            // Left and right edges: vertical runs. V spans the run, U sits mid-pixel.
            for (var px = left; px < right; px++)
            {
                for (var side = 0; side < 2; side++)
                {
                    var neighbour = side == 0 ? px - 1 : px + 1;
                    var x = side == 0 ? X(px) : X(px + 1);
                    var n = side == 0 ? -Vector3.UnitX : Vector3.UnitX;
                    var u = U(px + 0.5f);
                    var py = top;
                    while (py < bottom)
                    {
                        if (!Solid(px, py) || Solid(neighbour, py))
                        {
                            py++;
                            continue;
                        }

                        var runStart = py;
                        while (py < bottom && Solid(px, py) && !Solid(neighbour, py))
                            py++;

                        AddQuad(verts,
                            new Vector3(x, y0, Z(py)), new Vector3(x, y1, Z(py)),
                            new Vector3(x, y1, Z(runStart)), new Vector3(x, y0, Z(runStart)),
                            new Vector2(u, V(py - 0.5f)), new Vector2(u, V(py - 0.5f)),
                            new Vector2(u, V(runStart + 0.5f)), new Vector2(u, V(runStart + 0.5f)),
                            n, white);
                    }
                }
            }
        }

        private void EnsureWorld3DModelBuffer()
        {
            if (_world3DModelVbo == null)
            {
                _world3DModelVao = GenVertexArray();
                BindVertexArray(_world3DModelVao);
                _world3DModelVbo = new GLBuffer(this, BufferTarget.ArrayBuffer, BufferUsageHint.StaticDraw, "world3d-model-vbo");
                _world3DModelVbo.Use();
                SetupVertex3DLayout();
                BindVertexArray(0);
            }

            var total = _world3DModelVerts.Count;
            if (total == _world3DModelUploaded)
                return;

            var all = CollectionsMarshal.AsSpan(_world3DModelVerts);
            _world3DModelVbo.Use();
            if (total > _world3DModelCapacity)
            {
                // Grow (doubling) and re-upload everything.
                _world3DModelCapacity = Math.Max(total, Math.Max(65536, _world3DModelCapacity * 2));
                unsafe
                {
                    GL.BufferData(BufferTarget.ArrayBuffer, _world3DModelCapacity * sizeof(Vertex3D), IntPtr.Zero, BufferUsageHint.StaticDraw);
                }
                _world3DModelUploaded = 0;
            }

            var pending = all[_world3DModelUploaded..];
            unsafe
            {
                fixed (Vertex3D* p = pending)
                {
                    GL.BufferSubData(BufferTarget.ArrayBuffer, (IntPtr) (_world3DModelUploaded * sizeof(Vertex3D)),
                        pending.Length * sizeof(Vertex3D), (IntPtr) p);
                }
            }
            CheckGlError();
            _world3DModelUploaded = total;
        }

        private void DrawWorld3DModels(GLShaderProgram program)
        {
            if (_world3DInstances.Count == 0)
                return;

            BindVertexArray(_world3DModelVao);
            ClydeHandle bound = default;
            foreach (var inst in _world3DInstances)
            {
                if (inst.Texture != bound)
                {
                    SetTexture(TextureUnit.Texture0, inst.Texture);
                    bound = inst.Texture;
                }

                SetMatrix(program, "uModel", inst.Model);
                program.SetUniform("uTint", inst.Tint);
                GL.DrawArrays(PrimitiveType.Triangles, inst.Start, inst.Count);
                _debugStats.LastGLDrawCalls += 1;
            }

            program.SetUniform("uTint", Vector4.One);
            CheckGlError();
        }

        private void AddModelInstance(Texture frame, float depth, in Matrix4x4 placement, Vector4 tint, bool centerVertically = false)
        {
            var (tex, start, count, height) = GetExtrudedModel(frame, depth);
            var model = centerVertically
                ? Matrix4x4.CreateTranslation(0, 0, -height / 2f) * placement
                : placement;
            _world3DInstances.Add(new World3DInstance { Texture = tex, Start = start, Count = count, Model = model, Tint = tint });
        }

        #endregion

        #region Placement helpers

        /// <summary>Rotation about Z so that a model's front (-Y) faces along <paramref name="dir"/>.</summary>
        private static Matrix4x4 FaceAlong(Vector2 dir)
        {
            // Rotating (0, -1) by θ gives (sin θ, -cos θ).
            var theta = MathF.Atan2(dir.X, -dir.Y);
            return Matrix4x4.CreateRotationZ(theta);
        }

        private static readonly Matrix4x4 LayFlat = Matrix4x4.CreateRotationX(-MathF.PI / 2f);

        /// <summary>
        ///     Content pose (height, tip-over) as a world-space matrix to append to a placement:
        ///     tilt about the base point, then lift by the height.
        /// </summary>
        private Matrix4x4 PoseMatrix(EntityUid uid, Vector2 basePos, out bool known)
        {
            var pose = _world3D!.PoseProvider?.Invoke(uid) ?? World3DPose.Identity;
            known = pose.Known;

            var m = Matrix4x4.Identity;
            if (pose.Tilt > 0.001f && pose.TiltDirection.LengthSquared() > 1e-6f)
            {
                // Rotating "up" about (−d.y, d.x, 0) moves the top towards d.
                var d = Vector2.Normalize(pose.TiltDirection);
                var axis = new Vector3(-d.Y, d.X, 0);
                m = Matrix4x4.CreateTranslation(new Vector3(-basePos, 0))
                    * Matrix4x4.CreateFromAxisAngle(axis, pose.Tilt)
                    * Matrix4x4.CreateTranslation(new Vector3(basePos, 0));
            }

            if (pose.Z != 0f)
                m *= Matrix4x4.CreateTranslation(0, 0, pose.Z);

            return m;
        }

        private float PoseZ(EntityUid uid) => _world3D!.PoseProvider?.Invoke(uid).Z ?? 0f;

        private static (EntityUid Grid, Vector2i Tile) TileKey(in SpriteData entry)
        {
            var local = entry.Xform.LocalPosition;
            return (entry.Xform.ParentUid, new Vector2i((int) MathF.Floor(local.X), (int) MathF.Floor(local.Y)));
        }

        /// <summary>Grid rotation in world space (the entity's world rotation minus its local rotation).</summary>
        private static Angle GridRotation(in SpriteData entry) => entry.WorldRot - entry.Xform.LocalRotation;

        /// <summary>Centre of the entity's tile in world space.</summary>
        private static Vector2 TileCenterWorld(in SpriteData entry)
        {
            var local = entry.Xform.LocalPosition;
            var center = new Vector2(MathF.Floor(local.X) + 0.5f, MathF.Floor(local.Y) + 0.5f);
            return entry.WorldPos + GridRotation(entry).RotateVec(center - local);
        }

        private Texture? FrontFrame(SpriteComponent sprite, SpriteComponent.Layer layer)
            => GetLayerTexture(sprite, layer, Angle.Zero);

        /// <summary>Pushes an oriented box into the dynamic buffer, every face mapped with the same UV rect.</summary>
        private void PushOrientedBox(ClydeHandle tex, Vector2 center, Vector2 halfX, Vector2 halfY, float z0, float z1,
            Vector2 uvTL, Vector2 uvBR, Vector4 color)
        {
            var uvBL = new Vector2(uvTL.X, uvBR.Y);
            var uvTR = new Vector2(uvBR.X, uvTL.Y);
            Vector3 Lo(Vector2 p) => new(p, z0);
            Vector3 Hi(Vector2 p) => new(p, z1);

            var sw = center - halfX - halfY;
            var se = center + halfX - halfY;
            var ne = center + halfX + halfY;
            var nw = center - halfX + halfY;
            var nx = new Vector3(Vector2.Normalize(halfX), 0);
            var ny = new Vector3(Vector2.Normalize(halfY), 0);

            PushQuad(tex, Lo(sw), Lo(se), Hi(se), Hi(sw), uvBL, uvBR, uvTR, uvTL, -ny, color);
            PushQuad(tex, Lo(ne), Lo(nw), Hi(nw), Hi(ne), uvBL, uvBR, uvTR, uvTL, ny, color);
            PushQuad(tex, Lo(nw), Lo(sw), Hi(sw), Hi(nw), uvBL, uvBR, uvTR, uvTL, -nx, color);
            PushQuad(tex, Lo(se), Lo(ne), Hi(ne), Hi(se), uvBL, uvBR, uvTR, uvTL, nx, color);
            PushQuad(tex, Hi(sw), Hi(se), Hi(ne), Hi(nw), uvBL, uvBR, uvTR, uvTL, Vector3.UnitZ, color);
            PushQuad(tex, Lo(nw), Lo(ne), Lo(se), Lo(sw), uvTL, uvTR, uvBR, uvBL, -Vector3.UnitZ, color);
        }

        #endregion

        #region Shapes

        private void EmitProp(in SpriteData entry)
        {
            var sprite = entry.Sprite;
            var placement = Matrix4x4.CreateScale(sprite.Scale.X, sprite.Scale.X, sprite.Scale.Y * _render3DUprightScale)
                            * Matrix4x4.CreateRotationZ((float) entry.WorldRot.Theta)
                            * Matrix4x4.CreateTranslation(new Vector3(entry.WorldPos, 0));
            placement *= PoseMatrix(entry.Uid, entry.WorldPos, out _);

            for (var l = 0; l < sprite.Layers.Count; l++)
            {
                var layer = sprite.Layers[l];
                var frame = FrontFrame(sprite, layer);
                if (frame == null)
                    continue;

                // Overlay layers (lights, doors, screens) get slightly deeper so they sit proud of the base.
                AddModelInstance(frame, 0.7f + 0.012f * l, placement, LayerColor(sprite, layer));
            }
        }

        private void EmitMob(in SpriteData entry, Angle camYaw, Vector2 fwdFlat)
        {
            var sprite = entry.Sprite;
            var relative = entry.WorldRot - camYaw;
            var placement = Matrix4x4.CreateScale(sprite.Scale.X, sprite.Scale.X, sprite.Scale.Y * _render3DUprightScale)
                            * FaceAlong(-fwdFlat)
                            * Matrix4x4.CreateTranslation(new Vector3(entry.WorldPos, 0));
            placement *= PoseMatrix(entry.Uid, entry.WorldPos, out _);

            for (var l = 0; l < sprite.Layers.Count; l++)
            {
                var layer = sprite.Layers[l];
                var frame = GetLayerTexture(sprite, layer, relative);
                if (frame == null)
                    continue;

                // Clothing and hair layers wrap slightly outside the body.
                AddModelInstance(frame, 0.22f + 0.008f * l, placement, LayerColor(sprite, layer));
            }
        }

        private void EmitItem(in SpriteData entry)
        {
            var sprite = entry.Sprite;
            // Physics height when content knows it (includes tables); otherwise guess from a table on the tile.
            var pose = PoseMatrix(entry.Uid, entry.WorldPos, out var known);
            var surface = known ? 0f : _world3DTableTiles.Contains(TileKey(entry)) ? TableHeight : 0f;

            for (var l = 0; l < sprite.Layers.Count; l++)
            {
                var layer = sprite.Layers[l];
                var frame = GetLayerTexture(sprite, layer, entry.WorldRot);
                if (frame == null)
                    continue;

                var rot = layer.ActualState is { RsiDirections: not RsiDirectionType.Dir1 } || sprite.NoRotation
                    ? Angle.Zero
                    : entry.WorldRot;
                const float thickness = 0.035f;
                var placement = Matrix4x4.CreateScale(sprite.Scale.X * layer.Scale.X, 1, sprite.Scale.Y * layer.Scale.Y)
                                * LayFlat
                                * Matrix4x4.CreateRotationZ((float) rot.Theta)
                                * Matrix4x4.CreateTranslation(new Vector3(entry.WorldPos, surface + thickness / 2f + 0.002f * (l + 1)));
                placement *= pose;
                AddModelInstance(frame, thickness, placement, LayerColor(sprite, layer), centerVertically: true);
            }
        }

        private void EmitTable(in SpriteData entry)
        {
            var sprite = entry.Sprite;
            const float thickness = 0.07f;
            Texture? baseFrame = null;
            var baseColor = Vector4.One;

            for (var l = 0; l < sprite.Layers.Count; l++)
            {
                var layer = sprite.Layers[l];
                var frame = GetLayerTexture(sprite, layer, Angle.Zero);
                if (frame == null)
                    continue;

                if (baseFrame == null)
                {
                    baseFrame = frame;
                    baseColor = LayerColor(sprite, layer);
                }

                // Top-down table art lies flat as the table top. Smoothing corner layers each cover a quarter.
                var placement = LayFlat
                                * Matrix4x4.CreateRotationZ((float) GridRotation(entry).Theta)
                                * Matrix4x4.CreateTranslation(new Vector3(entry.WorldPos, TableHeight - thickness / 2f + 0.001f * l));
                AddModelInstance(frame, thickness, placement, LayerColor(sprite, layer), centerVertically: true);
            }

            if (baseFrame == null)
                return;

            // Pedestal in the table's own colour (centre pixel of the base frame).
            var (ct, uvTL, uvBR) = ResolveUV(baseFrame);
            var mid = (uvTL + uvBR) / 2f;
            var rot = GridRotation(entry);
            PushOrientedBox(ct.TextureId, entry.WorldPos,
                rot.RotateVec(new Vector2(0.08f, 0)), rot.RotateVec(new Vector2(0, 0.08f)),
                0f, TableHeight - thickness, mid, mid, baseColor * new Vector4(0.7f, 0.7f, 0.7f, 1f));
        }

        private void EmitWallMount(in SpriteData entry, Vector2 camFlat)
        {
            var sprite = entry.Sprite;
            var (grid, tile) = TileKey(entry);
            var gridRot = GridRotation(entry);
            var onWall = _world3DWallTiles.Contains((grid, tile));

            // Pick which wall face to hang on, as a grid-local direction pointing out of the wall.
            Vector2i outward = default;
            var found = false;

            if (onWall)
            {
                // Mounted on a wall tile: show it on the open face nearest the camera.
                var bestDot = float.MinValue;
                var toCam = camFlat - TileCenterWorld(entry);
                foreach (var dir in CardinalDirs)
                {
                    if (_world3DWallTiles.Contains((grid, tile + dir)))
                        continue;
                    var dot = Vector2.Dot(gridRot.RotateVec(dir), toCam);
                    if (dot > bestDot)
                    {
                        bestDot = dot;
                        outward = dir;
                        found = true;
                    }
                }
            }
            else
            {
                // Standing on a floor tile next to a wall: hang on that wall, preferring the entity's facing.
                var back = entry.Xform.LocalRotation.RotateVec(new Vector2(0, 1));
                var bestDot = float.MinValue;
                foreach (var dir in CardinalDirs)
                {
                    if (!_world3DWallTiles.Contains((grid, tile + dir)))
                        continue;
                    var dot = Vector2.Dot(dir, back);
                    if (dot > bestDot)
                    {
                        bestDot = dot;
                        outward = -dir;
                        found = true;
                    }
                }
            }

            if (!found)
            {
                // Free-standing: treat as a prop.
                EmitProp(entry);
                AddPickForShape(entry, World3DShape.Prop);
                return;
            }

            var outWorld = gridRot.RotateVec(outward);
            var faceCenter = onWall
                ? TileCenterWorld(entry) + outWorld * 0.5f
                : TileCenterWorld(entry) - outWorld * 0.5f;

            const float depth = 0.04f;
            var height = _entityManager.HasComponent<PointLightComponent>(entry.Uid) ? 2.0f : 1.25f;
            var placement = FaceAlong(outWorld)
                            * Matrix4x4.CreateTranslation(new Vector3(faceCenter + outWorld * (depth / 2f + 0.005f), height));

            var mountSize = FirstFrameSize(sprite);
            AddPick(entry.Uid, faceCenter + outWorld * 0.05f, new Vector2(outWorld.Y, -outWorld.X) * (mountSize.X / 2f),
                outWorld * 0.06f, height - mountSize.Y / 2f, height + mountSize.Y / 2f);

            for (var l = 0; l < sprite.Layers.Count; l++)
            {
                var layer = sprite.Layers[l];
                var frame = FrontFrame(sprite, layer);
                if (frame == null)
                    continue;

                var lifted = Matrix4x4.CreateTranslation(0, -0.003f * l, 0) * placement;
                AddModelInstance(frame, depth, lifted, LayerColor(sprite, layer), centerVertically: true);
            }
        }

        private void EmitPane(in SpriteData entry)
        {
            // Directional windows occupy the south edge of their tile in local space (fixture -0.5..-0.28).
            var rot = entry.WorldRot;
            var center = entry.WorldPos + rot.RotateVec(new Vector2(0, -0.39f));
            EmitPanel(entry, center, rot.RotateVec(new Vector2(0.5f, 0)), rot.RotateVec(new Vector2(0, 0.11f)));
        }

        private void EmitCenterPanel(in SpriteData entry, float halfThickness)
        {
            var (along, across) = PanelAxes(entry, halfThickness);
            EmitPanel(entry, TileCenterWorld(entry), along, across);
        }

        /// <summary>
        ///     World-space half-extents of a panel through the tile centre that runs along the wall line:
        ///     if walls sit north/south of the tile it spans grid Y, otherwise grid X.
        /// </summary>
        private (Vector2 Along, Vector2 Across) PanelAxes(in SpriteData entry, float halfThickness)
        {
            var (grid, tile) = TileKey(entry);
            var gridRot = GridRotation(entry);
            var wallsX = _world3DWallTiles.Contains((grid, tile + new Vector2i(1, 0)))
                         || _world3DWallTiles.Contains((grid, tile + new Vector2i(-1, 0)));
            var wallsY = _world3DWallTiles.Contains((grid, tile + new Vector2i(0, 1)))
                         || _world3DWallTiles.Contains((grid, tile + new Vector2i(0, -1)));

            return wallsY && !wallsX
                ? (gridRot.RotateVec(new Vector2(0, 0.5f)), gridRot.RotateVec(new Vector2(halfThickness, 0)))
                : (gridRot.RotateVec(new Vector2(0.5f, 0)), gridRot.RotateVec(new Vector2(0, halfThickness)));
        }

        /// <summary>
        ///     Doors are two half-panels that slide apart into the neighbouring walls as the door opens.
        ///     The base layers always show the door's "closed" art, so the panels look the same at any point
        ///     of the 2D open/close animation; overlay layers (lights, bolts, panels) only show while fully shut.
        /// </summary>
        private void EmitDoor(in SpriteData entry)
        {
            var open = Math.Clamp(_world3D!.DoorOpenAmount?.Invoke(entry.Uid) ?? 0f, 0f, 1f);
            if (open >= 0.999f)
                return; // Fully open: the halves are inside the walls (or there are none to hide in).

            var (along, across) = PanelAxes(entry, 0.12f);
            var dir = Vector2.Normalize(along);
            var center = TileCenterWorld(entry);
            var halfLen = along * 0.5f;
            var offset = 0.25f + 0.5f * open;
            var sprite = entry.Sprite;

            for (var l = 0; l < sprite.Layers.Count; l++)
            {
                var layer = sprite.Layers[l];
                var frame = ClosedDoorFrame(layer);
                if (frame == null)
                {
                    if (open > 0f)
                        continue;
                    frame = FrontFrame(sprite, layer);
                    if (frame == null)
                        continue;
                }

                var (ct, uvTL, uvBR) = ResolveUV(frame);
                var uMid = (uvTL.X + uvBR.X) / 2f;
                var grow = 1f + 0.01f * l;
                var color = LayerColor(sprite, layer);

                PushOrientedBox(ct.TextureId, center - dir * offset, halfLen * grow, across * grow, 0f, _render3DWallHeight,
                    uvTL, new Vector2(uMid, uvBR.Y), color);
                PushOrientedBox(ct.TextureId, center + dir * offset, halfLen * grow, across * grow, 0f, _render3DWallHeight,
                    new Vector2(uMid, uvTL.Y), uvBR, color);
            }
        }

        /// <summary>The "closed" frame for a layer that is showing one of the door cycle states, else null.</summary>
        private static Texture? ClosedDoorFrame(SpriteComponent.Layer layer)
        {
            if (!layer.Drawn || layer.ActualRsi is not { } rsi)
                return null;

            if (layer.State.Name is not ("closed" or "open" or "opening" or "closing"))
                return null;

            return rsi.TryGetState("closed", out var closed) ? closed.GetFrame(RsiDirection.South, 0) : null;
        }

        private void EmitPanel(in SpriteData entry, Vector2 center, Vector2 halfX, Vector2 halfY)
        {
            var sprite = entry.Sprite;
            for (var l = 0; l < sprite.Layers.Count; l++)
            {
                var layer = sprite.Layers[l];
                var frame = FrontFrame(sprite, layer);
                if (frame == null)
                    continue;

                var (ct, uvTL, uvBR) = ResolveUV(frame);
                var grow = 1f + 0.01f * l;
                PushOrientedBox(ct.TextureId, center, halfX * grow, halfY * grow, 0f, _render3DWallHeight,
                    uvTL, uvBR, LayerColor(sprite, layer));
            }
        }

        #endregion
    }
}

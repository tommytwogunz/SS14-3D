using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using OpenToolkit.Graphics.OpenGL4;
using Robust.Client.GameObjects;
using Robust.Client.Graphics.World3D;
using Robust.Client.Player;
using Robust.Shared;
using Robust.Shared.GameObjects;
using Robust.Shared.Graphics;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Components;
using Robust.Shared.Timing;
using DrawDepthTag = Robust.Shared.GameObjects.DrawDepth;

namespace Robust.Client.Graphics.Clyde
{
    // First-person 3D view of the world (render3d.enabled).
    // The simulation stays 2D: floors come from grid tiles, solid structures are extruded into boxes,
    // and everything else is drawn as camera-facing billboards of its existing sprite.
    // World axes: X east, Y north, Z up. 1 tile = 1 m.
    internal partial class Clyde
    {
        private const int World3DMaxLights = 48;
        private const float PixelsPerMeter3D = EyeManager.PixelsPerMeter;

        // Content draw depths we key off (see Content.Shared.DrawDepth). Relative to DrawDepth.Default.
        private const int DepthFlatMax = DrawDepthTag.Default - 11; // HighFloorObjects and below lie flat.
        private const int DepthWalls = DrawDepthTag.Default - 8;
        private const int DepthWallTops = DrawDepthTag.Default - 1;
        private const int DepthDoors = DrawDepthTag.Default + 8;
        private const int DepthBlastDoors = DrawDepthTag.Default + 9;
        private const int DepthDeadMobs = DrawDepthTag.Default - 10;
        private const int DepthSmallMobs = DrawDepthTag.Default - 9;
        private const int DepthSmallObjects = DrawDepthTag.Default + 1;
        private const int DepthWallMounted = DrawDepthTag.Default + 2;
        private const int DepthItems = DrawDepthTag.Default + 4;
        private const int DepthMobs = DrawDepthTag.Default + 6;
        private const int DepthGasses = DrawDepthTag.Default + 17;

        private bool _render3DEnabled;
        private float _render3DFov;
        private float _render3DWallHeight;
        private float _render3DEyeHeight;
        private float _render3DDrawDistance;
        private bool _render3DCeiling;
        private int _render3DDebugMask = 3;
        private bool _world3DDumpGrids;

        private IWorld3DManager? _world3D;
        private World3DDevBridge? _world3DDev;
        private IPlayerManager? _world3DPlayer;
        private IEyeManager? _world3DEyes;

        private GLShaderProgram? _world3DProgram;
        private uint _world3DDynVao;
        private GLBuffer? _world3DDynVbo;

        private readonly Dictionary<EntityUid, World3DGridMesh> _world3DGridMeshes = new();
        private List<Entity<MapGridComponent>> _world3DGrids = new();
        private readonly HashSet<EntityUid> _world3DSeenGrids = new();

        private Vertex3D[] _world3DVerts = new Vertex3D[16384];
        private int _world3DVertCount;
        private readonly List<World3DBatch> _world3DBatches = new();

        private readonly List<(float distSq, Vector4 posRadius, Vector4 color)> _world3DLights = new();
        private readonly float[] _world3DLightPosBuf = new float[World3DMaxLights * 4];
        private readonly float[] _world3DLightColBuf = new float[World3DMaxLights * 4];
        private readonly float[] _world3DMatBuf = new float[16];

        private static readonly (string, uint)[] World3DAttribLocations =
        {
            ("aPos", 0),
            ("aUV", 1),
            ("aNormal", 2),
            ("aColor", 3),
        };

        private const string World3DVertexSource = @"
in vec3 aPos;
in vec2 aUV;
in vec3 aNormal;
in vec4 aColor;

uniform mat4 uViewProj;
uniform mat4 uModel;

out vec3 vWorld;
out vec2 vUV;
out vec3 vNormal;
out vec4 vColor;

void main()
{
    vec4 world = uModel * vec4(aPos, 1.0);
    vWorld = world.xyz;
    vUV = aUV;
    vNormal = mat3(uModel) * aNormal;
    vColor = aColor;
    gl_Position = uViewProj * world;
}
";

        private const string World3DFragmentSource = @"
in vec3 vWorld;
in vec2 vUV;
in vec3 vNormal;
in vec4 vColor;

uniform sampler2D uTex;
uniform int uLightCount;
uniform vec4 uLightPosRadius[48];
uniform vec4 uLightColor[48];
uniform vec3 uAmbient;
uniform vec3 uAmbientGround;
uniform float uExposure;
uniform vec3 uCamPos;
uniform vec3 uFogColor;
uniform float uFogDistance;
uniform int uPass;
uniform int uDebugHeight;
uniform vec4 uTint;

out vec4 fragColor;

void main()
{
    vec4 tex = texture(uTex, vUV) * vColor * uTint;

    if (uDebugHeight != 0)
    {
        if (uPass != 0) discard;
        fragColor = vec4(clamp(vWorld.z / 3.0, 0.0, 1.0), 0.15, 1.0 - clamp(vWorld.z / 3.0, 0.0, 1.0), 1.0);
        return;
    }

    // Pass 0: opaque, writes depth. Pass 1: translucent leftovers (glass, soft sprite edges).
    if (uPass == 0)
    {
        if (tex.a < 0.9) discard;
    }
    else
    {
        if (tex.a >= 0.9 || tex.a < 0.02) discard;
    }

    vec3 n = normalize(vNormal);
    if (!gl_FrontFacing) n = -n;

    // Hemisphere ambient: surfaces facing up catch more light than undersides, so every shape reads as solid.
    vec3 light = mix(uAmbientGround, uAmbient, n.z * 0.5 + 0.5);
    for (int i = 0; i < uLightCount; i++)
    {
        vec3 d = uLightPosRadius[i].xyz - vWorld;
        float dist = length(d);
        float radius = uLightPosRadius[i].w;
        if (dist >= radius) continue;
        // Smooth windowed falloff (no hard ring at the radius), soft wrap-around lighting.
        float f = dist / radius;
        float att = (1.0 - f * f);
        att = att * att / (1.0 + 4.0 * f * f);
        float ndl = clamp(dot(n, d / max(dist, 0.0001)) * 0.6 + 0.4, 0.0, 1.0);
        light += uLightColor[i].rgb * att * ndl;
    }

    // Exposure tonemap: compresses bright pools and lifts shadows so the mixed art reads as one image.
    vec3 color = vec3(1.0) - exp(-tex.rgb * light * uExposure);
    float fog = clamp(length(vWorld - uCamPos) / uFogDistance, 0.0, 1.0);
    color = mix(color, uFogColor, fog * fog);
    fragColor = vec4(color, tex.a);
}
";

        [StructLayout(LayoutKind.Sequential)]
        private struct Vertex3D
        {
            public Vector3 Position;
            public Vector2 UV;
            public Vector3 Normal;
            public Vector4 Color;

            public Vertex3D(Vector3 position, Vector2 uv, Vector3 normal, Vector4 color)
            {
                Position = position;
                UV = uv;
                Normal = normal;
                Color = color;
            }
        }

        private struct World3DBatch
        {
            public ClydeHandle Texture;
            public int Start;
            public int Count;
        }

        private sealed class World3DGridMesh
        {
            public uint Vao;
            public GLBuffer Vbo = default!;
            public int VertexCount;
            public int CeilingStart;
            public GameTick BuiltTick;
            public int BuiltChunkCount = -1;
            public string DebugFirst = "";
        }

        private void InitWorld3D()
        {
            _cfg.OnValueChanged(CVars.Render3DEnabled, v => _render3DEnabled = v, true);
            _cfg.OnValueChanged(CVars.Render3DFov, v => _render3DFov = v, true);
            _cfg.OnValueChanged(CVars.Render3DWallHeight, v => _render3DWallHeight = v, true);
            _cfg.OnValueChanged(CVars.Render3DEyeHeight, v => _render3DEyeHeight = v, true);
            _cfg.OnValueChanged(CVars.Render3DDrawDistance, v => _render3DDrawDistance = v, true);
            _cfg.OnValueChanged(CVars.Render3DCeiling, v => _render3DCeiling = v, true);
            _cfg.OnValueChanged(CVars.Render3DDebugMask, v => _render3DDebugMask = v, true);
            InitWorld3DModels();
        }

        /// <summary>
        ///     True if this viewport should be drawn by <see cref="RenderWorld3D"/> instead of the 2D world pass.
        ///     Only the viewport looking through the current (player) eye goes 3D; camera monitors etc. stay 2D.
        /// </summary>
        private bool ShouldRenderWorld3D(Viewport viewport)
        {
            if (!_render3DEnabled || viewport.Eye == null)
                return false;

            _world3DEyes ??= _deps.Resolve<IEyeManager>();
            return ReferenceEquals(viewport.Eye, _world3DEyes.CurrentEye);
        }

        private void EnsureWorld3DResources()
        {
            if (_world3DProgram != null)
                return;

            _world3DProgram = _compileProgram(World3DVertexSource, World3DFragmentSource, World3DAttribLocations,
                "world3d", includeLib: false);

            _world3DDynVao = GenVertexArray();
            BindVertexArray(_world3DDynVao);
            _world3DDynVbo = new GLBuffer(this, BufferTarget.ArrayBuffer, BufferUsageHint.StreamDraw, "world3d-dyn-vbo");
            _world3DDynVbo.Use();
            SetupVertex3DLayout();
            BindVertexArray(0);
        }

        private static unsafe void SetupVertex3DLayout()
        {
            var stride = sizeof(Vertex3D);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, stride, 5 * sizeof(float));
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, 8 * sizeof(float));
            GL.EnableVertexAttribArray(3);
        }

        private void RenderWorld3D(Viewport viewport, IEye eye)
        {
            EnsureWorld3DResources();
            _world3D ??= _deps.Resolve<IWorld3DManager>();
            _world3DPlayer ??= _deps.Resolve<IPlayerManager>();

            var mapId = eye.Position.MapId;
            var camFlat = _world3D.CameraPosition ?? eye.Position.Position;
            var camHeight = float.IsFinite(_world3D.CameraHeight) ? _world3D.CameraHeight : _render3DEyeHeight;
            var camPos = new Vector3(camFlat, camHeight);
            var forward = _world3D.Forward;

            var size = viewport.RenderTarget.Size;
            var aspect = size.X / (float) Math.Max(1, size.Y);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(
                MathHelper.DegreesToRadians(Math.Clamp(_render3DFov, 30f, 140f)), aspect, 0.05f, 400f);
            var view = Matrix4x4.CreateLookAt(camPos, camPos + forward, Vector3.UnitZ);
            var viewProj = view * proj;

            var program = _world3DProgram!;

            // GL state for a depth-tested 3D pass. Clyde normally draws 2D with depth off.
            IsBlending = false;
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Lequal);
            GL.DepthMask(true);
            GL.Disable(EnableCap.CullFace);
            // Clyde keeps primitive restart (index 65535) on for its 2D quad batches. Some drivers (NVIDIA) apply it to
            // non-indexed DrawArrays too, which shifts triangle assembly by one after vertex 65535 and mixes up meshes.
            SetPrimitiveRestart(false);
            GL.ClearDepth(1);
            var fog = new Vector3(0.004f, 0.004f, 0.008f);
            GL.ClearColor(fog.X, fog.Y, fog.Z, 1);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            CheckGlError();

            if (_world3D.DumpNextFrame)
            {
                GL.GetFramebufferAttachmentParameter(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                    FramebufferParameterName.FramebufferAttachmentDepthSize, out int depthBits);
                GL.GetInteger(GetPName.FramebufferBinding, out int fbo);
                _clydeSawmill.Info($"world3d gl: fbo={fbo} depthBits={depthBits} depthTest={GL.IsEnabled(EnableCap.DepthTest)} cam={camPos} fwd={forward} size={size}");
                CheckGlError();
            }

            program.Use();
            SetMatrix(program, "uViewProj", viewProj);
            program.SetUniform("uCamPos", camPos);
            program.SetUniform("uAmbient", new Vector3(0.20f, 0.21f, 0.25f));
            program.SetUniform("uAmbientGround", new Vector3(0.07f, 0.07f, 0.08f));
            program.SetUniform("uExposure", 1.6f);
            program.SetUniform("uFogColor", fog);
            program.SetUniform("uFogDistance", Math.Max(8f, _render3DDrawDistance));
            program.SetUniformTextureMaybe("uTex", TextureUnit.Texture0);
            program.SetUniform("uTint", Vector4.One);
            program.SetUniform("uDebugHeight", (_render3DDebugMask & 4) != 0 ? 1 : 0);

            GatherWorld3DLights(mapId, camFlat);
            UploadWorld3DLights(program);

            // Build dynamic geometry (entities) once; draw it in both passes.
            BuildWorld3DEntities(viewport, eye, mapId, camFlat);
            BuildWorld3DViewModel(camPos);
            EmitWorld3DBeams(camPos);
            _world3DDynVbo!.Use();
            _world3DDynVbo.Reallocate(new Span<Vertex3D>(_world3DVerts, 0, _world3DVertCount));
            EnsureWorld3DModelBuffer();

            for (var pass = 0; pass < 2; pass++)
            {
                program.SetUniform("uPass", pass);
                if (pass == 1)
                {
                    IsBlending = true;
                    ResetBlendFunc();
                    GL.DepthMask(false);
                }

                if ((_render3DDebugMask & 1) != 0)
                    DrawWorld3DGrids(program, mapId, camFlat);

                SetMatrix(program, "uModel", Matrix4x4.Identity);
                BindVertexArray(_world3DDynVao);
                if ((_render3DDebugMask & 2) != 0)
                {
                    foreach (var batch in _world3DBatches)
                    {
                        EnsureMipmaps(batch.Texture);
                        SetTexture(TextureUnit.Texture0, batch.Texture);
                        GL.DrawArrays(PrimitiveType.Triangles, batch.Start, batch.Count);
                        _debugStats.LastGLDrawCalls += 1;
                    }

                    DrawWorld3DModels(program);
                }
                CheckGlError();
            }

            if ((_render3DDebugMask & 2) != 0)
                DrawWorld3DViewModel(program);

            // Restore the state Clyde's 2D batching expects.
            GL.DepthMask(false);
            SetPrimitiveRestart(true);
            GL.Disable(EnableCap.DepthTest);
            BindVertexArray(0);
            IsBlending = true;
            ResetBlendFunc();
            CheckGlError();

            _world3DDumpGrids = false;
            _world3DBatches.Clear();
            _world3DInstances.Clear();
            FinishWorld3DPicking(viewProj, camPos, size, mapId);
            _world3DVertCount = 0;
        }

        private void DrawWorld3DGrids(GLShaderProgram program, MapId mapId, Vector2 camFlat)
        {
            _world3DGrids.Clear();
            var range = Math.Max(_render3DDrawDistance * 3f, 64f);
            var box = Box2.CenteredAround(camFlat, new Vector2(range * 2, range * 2));
            _mapSystem.FindGridsIntersecting(mapId, box, ref _world3DGrids);

            EnsureMipmaps(((ClydeTexture) _tileDefinitionManager.TileTextureAtlas).TextureId);
            SetTexture(TextureUnit.Texture0, _tileDefinitionManager.TileTextureAtlas);
            _world3DSeenGrids.Clear();

            foreach (var grid in _world3DGrids)
            {
                _world3DSeenGrids.Add(grid.Owner);
                var mesh = EnsureWorld3DGridMesh(grid);
                if (mesh.VertexCount == 0)
                    continue;

                var m = _transformSystem.GetWorldMatrix(grid.Owner);
                if (_world3DDumpGrids)
                    _clydeSawmill.Info($"world3d grid {grid.Owner}: verts={mesh.VertexCount} chunks={grid.Comp.ChunkCount} m=[{m.M11:0.###} {m.M12:0.###} {m.M21:0.###} {m.M22:0.###} {m.M31:0.###} {m.M32:0.###}] first={(mesh.DebugFirst)}");
                var model = new Matrix4x4(
                    m.M11, m.M12, 0, 0,
                    m.M21, m.M22, 0, 0,
                    0, 0, 1, 0,
                    m.M31, m.M32, 0, 1);
                SetMatrix(program, "uModel", model);
                BindVertexArray(mesh.Vao);
                GL.DrawArrays(PrimitiveType.Triangles, 0, mesh.CeilingStart);
                if (mesh.VertexCount > mesh.CeilingStart)
                {
                    SetTexture(TextureUnit.Texture0, GetCeilingMaterial());
                    GL.DrawArrays(PrimitiveType.Triangles, mesh.CeilingStart, mesh.VertexCount - mesh.CeilingStart);
                    SetTexture(TextureUnit.Texture0, _tileDefinitionManager.TileTextureAtlas);
                    _debugStats.LastGLDrawCalls += 1;
                }
                _debugStats.LastGLDrawCalls += 1;
            }

            // Free meshes for grids that are gone (deleted, or a different map).
            if (_world3DGridMeshes.Count > _world3DSeenGrids.Count)
            {
                foreach (var (uid, mesh) in _world3DGridMeshes)
                {
                    if (_world3DSeenGrids.Contains(uid) || _entityManager.EntityExists(uid))
                        continue;

                    mesh.Vbo.Delete();
                    DeleteVertexArray(mesh.Vao);
                    _world3DGridMeshes.Remove(uid);
                }
            }
        }

        private World3DGridMesh EnsureWorld3DGridMesh(Entity<MapGridComponent> grid)
        {
            if (!_world3DGridMeshes.TryGetValue(grid.Owner, out var mesh))
            {
                mesh = new World3DGridMesh();
                mesh.Vao = GenVertexArray();
                BindVertexArray(mesh.Vao);
                mesh.Vbo = new GLBuffer(this, BufferTarget.ArrayBuffer, BufferUsageHint.StaticDraw, "world3d-grid-vbo");
                mesh.Vbo.Use();
                SetupVertex3DLayout();
                BindVertexArray(0);
                _world3DGridMeshes.Add(grid.Owner, mesh);
            }

            var comp = grid.Comp;
            if (mesh.BuiltTick == comp.LastTileModifiedTick && mesh.BuiltChunkCount == comp.ChunkCount)
                return mesh;

            var verts = new List<Vertex3D>(comp.ChunkCount * comp.ChunkSize * comp.ChunkSize * 12);
            var floorColor = new Vector4(1, 1, 1, 1);
            var ceilColor = Vector4.One;
            var h = _render3DWallHeight;
            var ceilVerts = new List<Vertex3D>();
            // Every ceiling shares one panel material (drawn as a second range of this mesh).
            var (_, ceilTL, ceilBR) = ResolveUV(GetCeilingMaterial());
            var ceilUv0 = ceilTL;
            var ceilUv1 = new Vector2(ceilBR.X, ceilTL.Y);
            var ceilUv2 = ceilBR;
            var ceilUv3 = new Vector2(ceilTL.X, ceilBR.Y);

            var enumerator = _mapSystem.GetAllTilesEnumerator(grid.Owner, comp);
            while (enumerator.MoveNext(out var tileRef))
            {
                var tile = tileRef.Value.Tile;
                if (tile.IsEmpty)
                    continue;

                var regions = _tileDefinitionManager.TileAtlasRegion(tile);
                var region = regions == null || regions.Length <= tile.Variant
                    ? _tileDefinitionManager.ErrorTileRegion
                    : regions[tile.Variant];

                var x = (float) tileRef.Value.GridIndices.X;
                var y = (float) tileRef.Value.GridIndices.Y;

                // Floor, facing up. Same UV orientation as the 2D grid renderer.
                AddQuad(verts,
                    new Vector3(x, y, 0), new Vector3(x + 1, y, 0), new Vector3(x + 1, y + 1, 0), new Vector3(x, y + 1, 0),
                    new Vector2(region.Left, region.Bottom), new Vector2(region.Right, region.Bottom),
                    new Vector2(region.Right, region.Top), new Vector2(region.Left, region.Top),
                    Vector3.UnitZ, floorColor);

                if (_render3DCeiling)
                {
                    AddQuad(ceilVerts,
                        new Vector3(x, y + 1, h), new Vector3(x + 1, y + 1, h), new Vector3(x + 1, y, h), new Vector3(x, y, h),
                        ceilUv0, ceilUv1, ceilUv2, ceilUv3,
                        -Vector3.UnitZ, ceilColor);
                }
            }

            // Ceilings go after all floors so the two never share a draw range.
            mesh.CeilingStart = verts.Count;
            verts.AddRange(ceilVerts);

            var span = CollectionsMarshal.AsSpan(verts);
            BindVertexArray(mesh.Vao);
            mesh.Vbo.Use();
            mesh.Vbo.Reallocate(span);
            BindVertexArray(0);
            mesh.VertexCount = verts.Count;
            mesh.DebugFirst = verts.Count >= 6 ? $"{verts[0].Position} {verts[1].Position} {verts[2].Position} {verts[5].Position}" : "";
            mesh.BuiltTick = comp.LastTileModifiedTick;
            mesh.BuiltChunkCount = comp.ChunkCount;
            return mesh;
        }

        private void BuildWorld3DEntities(Viewport viewport, IEye eye, MapId mapId, Vector2 camFlat)
        {
            _world3DBatches.Clear();
            _world3DVertCount = 0;

            var range = _render3DDrawDistance;
            var bounds = new Box2Rotated(Box2.CenteredAround(camFlat, new Vector2(range * 2, range * 2)), Angle.Zero, camFlat);
            ProcessSpriteEntities(mapId, viewport, eye, bounds, _drawingSpriteList);

            var local = _world3DPlayer!.LocalEntity;
            var classifier = _world3D!.ShapeClassifier;
            var physicsQuery = _entityManager.GetEntityQuery<PhysicsComponent>();
            var camYaw = new Angle(_world3D.Yaw);
            var fwdFlat = _world3D.ForwardFlat;
            var right = new Vector2(fwdFlat.Y, -fwdFlat.X);
            var rangeSq = range * range;
            var dump = _world3D.DumpNextFrame;
            _world3DDumpGrids = dump;
            _world3D.DumpNextFrame = false;
            if (dump)
                _clydeSawmill.Info($"world3d dump: cam {camFlat} yaw {_world3D.Yaw} pitch {_world3D.Pitch}, {_drawingSpriteList.Count} sprites");

            // Phase 1: classify everything and record which tiles hold walls and tables,
            // since wall mounts, doors and items placed on tables depend on their neighbours.
            var count = _drawingSpriteList.Count;
            if (_world3DShapes.Length < count)
                Array.Resize(ref _world3DShapes, Math.Max(count, _world3DShapes.Length * 2));
            _world3DWallTiles.Clear();
            _world3DTableTiles.Clear();

            for (var i = 0; i < count; i++)
            {
                ref var entry = ref _drawingSpriteList[i];
                if (entry.Uid == local || (entry.WorldPos - camFlat).LengthSquared() > rangeSq)
                {
                    _world3DShapes[i] = World3DShape.Hidden;
                    continue;
                }

                var shape = classifier?.Invoke(entry.Uid) ?? World3DShape.Auto;
                if (shape == World3DShape.Auto)
                    shape = ClassifyWorld3D(entry, physicsQuery);
                _world3DShapes[i] = shape;

                if (shape == World3DShape.Box)
                    _world3DWallTiles.Add(TileKey(entry));
                else if (shape == World3DShape.Table)
                    _world3DTableTiles.Add(TileKey(entry));

                if (dump)
                    DumpWorld3DEntry(entry, shape, camFlat);
            }

            // Phase 2: emit geometry.
            for (var i = 0; i < count; i++)
            {
                ref var entry = ref _drawingSpriteList[i];
                AddPickForShape(entry, _world3DShapes[i]);
                switch (_world3DShapes[i])
                {
                    case World3DShape.Hidden:
                        continue;
                    case World3DShape.Box:
                        EmitBox(entry);
                        break;
                    case World3DShape.Flat:
                        EmitFlat(entry);
                        break;
                    case World3DShape.Prop:
                        EmitProp(entry);
                        break;
                    case World3DShape.Mob:
                        EmitMob(entry, camYaw, fwdFlat);
                        break;
                    case World3DShape.Item:
                        EmitItem(entry);
                        break;
                    case World3DShape.Table:
                        EmitTable(entry);
                        break;
                    case World3DShape.WallMount:
                        EmitWallMount(entry, camFlat);
                        break;
                    case World3DShape.Pane:
                        EmitPane(entry);
                        break;
                    case World3DShape.Door:
                        EmitDoor(entry);
                        break;
                    case World3DShape.Fence:
                        EmitCenterPanel(entry, 0.03f);
                        break;
                    default:
                        EmitBillboard(entry, camYaw, right, fwdFlat);
                        break;
                }
            }

            _drawingSpriteList.Clear();
        }

        private void DumpWorld3DEntry(in SpriteData entry, World3DShape shape, Vector2 camFlat)
        {
            var proto = _entityManager.GetComponentOrNull<MetaDataComponent>(entry.Uid)?.EntityPrototype?.ID ?? "?";
            var sizes = "";
            foreach (var layer in entry.Sprite.Layers)
            {
                var tex = GetLayerTexture(entry.Sprite, layer, Angle.Zero);
                if (tex != null)
                    sizes += $"{tex.Width}x{tex.Height} ";
            }

            var pose = _world3D!.PoseProvider?.Invoke(entry.Uid) ?? World3DPose.Identity;
            _clydeSawmill.Info($"  {entry.Uid} {proto} {shape} depth={entry.Sprite.DrawDepth} pos={entry.WorldPos} d={(entry.WorldPos - camFlat).Length():0.0} scale={entry.Sprite.Scale} off={entry.Sprite.Offset} tex=[{sizes}] z={pose.Z:0.00} tilt={pose.Tilt:0.00}");
        }

        private World3DShape ClassifyWorld3D(in SpriteData entry, EntityQuery<PhysicsComponent> physicsQuery)
        {
            var depth = entry.Sprite.DrawDepth;
            if (depth <= DepthFlatMax)
                return World3DShape.Flat;

            var solid = entry.Xform.Anchored
                        && physicsQuery.TryGetComponent(entry.Uid, out var body)
                        && body.CanCollide && body.Hard;

            switch (depth)
            {
                case DepthWalls or DepthWallTops:
                    return solid ? World3DShape.Box : World3DShape.WallMount;
                case DepthDoors or DepthBlastDoors:
                    return solid ? World3DShape.Door : World3DShape.Hidden;
                case DepthDeadMobs:
                    return World3DShape.Item;
                case DepthSmallMobs or DepthMobs:
                    return World3DShape.Mob;
                case DepthWallMounted:
                    return World3DShape.WallMount;
                case DepthItems:
                    return World3DShape.Item;
                case DepthSmallObjects:
                    return entry.Xform.Anchored ? World3DShape.Prop : World3DShape.Item;
                case >= DepthGasses:
                    return World3DShape.Billboard;
                default:
                    return World3DShape.Prop;
            }
        }

        /// <summary>
        ///     Resolves the texture a sprite layer would draw for the given view-relative rotation.
        /// </summary>
        private static Texture? GetLayerTexture(SpriteComponent sprite, SpriteComponent.Layer layer, Angle relativeRotation)
        {
            if (!layer.Drawn)
                return null;

            if (layer.ActualState is { } state)
            {
                Direction? overrideDir = sprite.EnableDirectionOverride ? sprite.DirectionOverride : null;
                var dir = layer.EffectiveDirection(state, relativeRotation, overrideDir);
                var frames = state.GetFrames(dir);
                if (frames.Length == 0)
                    return null;
                return frames[Math.Clamp(layer.AnimationFrame, 0, frames.Length - 1)];
            }

            return layer.Texture;
        }

        private static Vector4 LayerColor(SpriteComponent sprite, SpriteComponent.Layer layer)
        {
            var c = Color.FromSrgb(sprite.Color * layer.Color);
            return new Vector4(c.R, c.G, c.B, c.A);
        }

        private static (ClydeTexture tex, Vector2 uvTL, Vector2 uvBR) ResolveUV(Texture texture)
        {
            var ct = RenderHandle.ExtractTexture(texture, null, out var sr);
            var (w, h) = ct.Size;
            // Clyde textures are flipped vertically: image top (sr.Top) maps to v = (h - top) / h.
            var uvTL = new Vector2(sr.Left / w, (h - sr.Top) / h);
            var uvBR = new Vector2(sr.Right / w, (h - sr.Bottom) / h);
            return (ct, uvTL, uvBR);
        }

        private void EmitBillboard(in SpriteData entry, Angle camYaw, Vector2 right, Vector2 fwdFlat)
        {
            var sprite = entry.Sprite;
            var relative = entry.WorldRot - camYaw;
            var scale = sprite.Scale;
            var basePos = entry.WorldPos + sprite.Offset;

            for (var l = 0; l < sprite.Layers.Count; l++)
            {
                var layer = sprite.Layers[l];
                var texture = GetLayerTexture(sprite, layer, relative);
                if (texture == null)
                    continue;

                var (ct, uvTL, uvBR) = ResolveUV(texture);
                var w = texture.Width / PixelsPerMeter3D * scale.X * layer.Scale.X;
                var hgt = texture.Height / PixelsPerMeter3D * scale.Y * layer.Scale.Y;

                // Layers stack toward the camera a hair so they don't z-fight.
                var center = basePos + right * layer.Offset.X - fwdFlat * (0.002f * l);
                var zBase = layer.Offset.Y;
                var halfW = right * (w / 2f);

                var bl = new Vector3(center - halfW, zBase);
                var br = new Vector3(center + halfW, zBase);
                var tr = new Vector3(center + halfW, zBase + hgt);
                var tl = new Vector3(center - halfW, zBase + hgt);
                var normal = new Vector3(-fwdFlat, 0);

                PushQuad(ct.TextureId, bl, br, tr, tl,
                    new Vector2(uvTL.X, uvBR.Y), uvBR, new Vector2(uvBR.X, uvTL.Y), uvTL,
                    normal, LayerColor(sprite, layer));
            }
        }

        private void EmitFlat(in SpriteData entry)
        {
            var sprite = entry.Sprite;
            var z = 0.004f + 0.0006f * (entry.Sprite.DrawDepth - (DrawDepthTag.Default - 30));

            for (var l = 0; l < sprite.Layers.Count; l++)
            {
                var layer = sprite.Layers[l];
                // Top-down sprites: direction is picked from world rotation exactly like the 2D view with eye rotation 0.
                var texture = GetLayerTexture(sprite, layer, entry.WorldRot);
                if (texture == null)
                    continue;

                var (ct, uvTL, uvBR) = ResolveUV(texture);
                var half = new Vector2(texture.Width, texture.Height) / PixelsPerMeter3D / 2f * sprite.Scale * layer.Scale;

                // Single-direction states rotate with the entity; directional states already face the right way.
                var rot = layer.ActualState is { RsiDirections: not RsiDirectionType.Dir1 } || sprite.NoRotation
                    ? Angle.Zero
                    : entry.WorldRot;
                var center = entry.WorldPos + rot.RotateVec(sprite.Offset + layer.Offset);
                var zl = z + 0.0001f * l;

                var bl = new Vector3(center + rot.RotateVec(new Vector2(-half.X, -half.Y)), zl);
                var br = new Vector3(center + rot.RotateVec(new Vector2(half.X, -half.Y)), zl);
                var tr = new Vector3(center + rot.RotateVec(new Vector2(half.X, half.Y)), zl);
                var tl = new Vector3(center + rot.RotateVec(new Vector2(-half.X, half.Y)), zl);

                PushQuad(ct.TextureId, bl, br, tr, tl,
                    new Vector2(uvTL.X, uvBR.Y), uvBR, new Vector2(uvBR.X, uvTL.Y), uvTL,
                    Vector3.UnitZ, LayerColor(sprite, layer));
            }
        }

        private void EmitBox(in SpriteData entry)
        {
            var sprite = entry.Sprite;

            // Generated panel material in the wall's own colours (see Clyde.World3DMaterials).
            if (GetWallMaterial(sprite) is not var (material, _))
                return;

            var color = sprite.Layers.Count > 0 ? LayerColor(sprite, sprite.Layers[0]) : Vector4.One;
            var (ct, uvTL, uvBR) = ResolveUV(material);
            var uvBL = new Vector2(uvTL.X, uvBR.Y);
            var uvTR = new Vector2(uvBR.X, uvTL.Y);

            // Boxes follow the entity's world rotation so walls line up with rotated station grids.
            var c = entry.WorldPos;
            var ax = entry.WorldRot.RotateVec(new Vector2(0.5f, 0));
            var ay = entry.WorldRot.RotateVec(new Vector2(0, 0.5f));
            var z1 = _render3DWallHeight;
            var tex = ct.TextureId;

            Vector3 Hi(Vector2 p) => new(p, z1);

            var sw = c - ax - ay;
            var se = c + ax - ay;
            var ne = c + ax + ay;
            var nw = c - ax + ay;
            var nx = new Vector3(Vector2.Normalize(ax), 0);
            var ny = new Vector3(Vector2.Normalize(ay), 0);

            // Local south, north, west, east sides tile the 1 m sprite once per metre of height; then the top.
            PushTiledSide(tex, sw, se, z1, uvTL, uvBR, -ny, color);
            PushTiledSide(tex, ne, nw, z1, uvTL, uvBR, ny, color);
            PushTiledSide(tex, nw, sw, z1, uvTL, uvBR, -nx, color);
            PushTiledSide(tex, se, ne, z1, uvTL, uvBR, nx, color);
            // The top meets the ceiling; keep it darker so wall tops seen from above read as wall, not floor.
            PushQuad(tex, Hi(sw), Hi(se), Hi(ne), Hi(nw), uvBL, uvBR, uvTR, uvTL, Vector3.UnitZ,
                color * new Vector4(0.6f, 0.6f, 0.6f, 1f));
        }

        /// <summary>
        ///     A vertical face from floor to <paramref name="height"/>, split into 1 m segments so a 1 m sprite repeats
        ///     instead of stretching. The last segment shows the bottom part of the sprite.
        /// </summary>
        private void PushTiledSide(ClydeHandle tex, Vector2 a, Vector2 b, float height, Vector2 uvTL, Vector2 uvBR,
            Vector3 normal, Vector4 color)
        {
            for (var z0 = 0f; z0 < height - 0.001f; z0 += 1f)
            {
                var z1 = MathF.Min(z0 + 1f, height);
                var vTop = uvBR.Y + (uvTL.Y - uvBR.Y) * (z1 - z0);
                PushQuad(tex,
                    new Vector3(a, z0), new Vector3(b, z0), new Vector3(b, z1), new Vector3(a, z1),
                    new Vector2(uvTL.X, uvBR.Y), uvBR, new Vector2(uvBR.X, vTop), new Vector2(uvTL.X, vTop),
                    normal, color);
            }
        }

        private void PushQuad(ClydeHandle texture,
            Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
            Vector2 t0, Vector2 t1, Vector2 t2, Vector2 t3,
            Vector3 normal, Vector4 color)
        {
            if (_world3DVertCount + 6 > _world3DVerts.Length)
                Array.Resize(ref _world3DVerts, _world3DVerts.Length * 2);

            // Merge into the previous batch when the texture matches (sprite atlases make this common).
            if (_world3DBatches.Count > 0 && _world3DBatches[^1].Texture == texture)
            {
                var last = _world3DBatches[^1];
                last.Count += 6;
                _world3DBatches[^1] = last;
            }
            else
            {
                _world3DBatches.Add(new World3DBatch { Texture = texture, Start = _world3DVertCount, Count = 6 });
            }

            var v = _world3DVerts;
            var i = _world3DVertCount;
            v[i + 0] = new Vertex3D(p0, t0, normal, color);
            v[i + 1] = new Vertex3D(p1, t1, normal, color);
            v[i + 2] = new Vertex3D(p2, t2, normal, color);
            v[i + 3] = new Vertex3D(p0, t0, normal, color);
            v[i + 4] = new Vertex3D(p2, t2, normal, color);
            v[i + 5] = new Vertex3D(p3, t3, normal, color);
            _world3DVertCount += 6;
        }

        private static void AddQuad(List<Vertex3D> verts,
            Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
            Vector2 t0, Vector2 t1, Vector2 t2, Vector2 t3,
            Vector3 normal, Vector4 color)
        {
            verts.Add(new Vertex3D(p0, t0, normal, color));
            verts.Add(new Vertex3D(p1, t1, normal, color));
            verts.Add(new Vertex3D(p2, t2, normal, color));
            verts.Add(new Vertex3D(p0, t0, normal, color));
            verts.Add(new Vertex3D(p2, t2, normal, color));
            verts.Add(new Vertex3D(p3, t3, normal, color));
        }

        private void GatherWorld3DLights(MapId mapId, Vector2 camFlat)
        {
            _world3DLights.Clear();
            var reach = _render3DDrawDistance + 10f;
            var lightHeight = _render3DWallHeight - 0.35f;

            var query = _entityManager.EntityQueryEnumerator<PointLightComponent, TransformComponent>();
            while (query.MoveNext(out var light, out var xform))
            {
                if (!light.Enabled || light.ContainerOccluded || xform.MapID != mapId)
                    continue;

                var (pos, rot) = _transformSystem.GetWorldPositionRotation(xform);
                pos += rot.RotateVec(light.Offset);
                var distSq = (pos - camFlat).LengthSquared();
                var radius = Math.Max(light.Radius, 0.5f);
                if (distSq > (reach + radius) * (reach + radius))
                    continue;

                var col = Color.FromSrgb(light.Color);
                var energy = light.Energy;
                _world3DLights.Add((distSq,
                    new Vector4(pos.X, pos.Y, lightHeight, radius + 1f),
                    new Vector4(col.R * energy, col.G * energy, col.B * energy, 1)));
            }

            _world3DLights.Sort((a, b) => a.distSq.CompareTo(b.distSq));
        }

        private void UploadWorld3DLights(GLShaderProgram program)
        {
            var count = Math.Min(_world3DLights.Count, World3DMaxLights);
            for (var i = 0; i < count; i++)
            {
                var (_, p, c) = _world3DLights[i];
                _world3DLightPosBuf[i * 4 + 0] = p.X;
                _world3DLightPosBuf[i * 4 + 1] = p.Y;
                _world3DLightPosBuf[i * 4 + 2] = p.Z;
                _world3DLightPosBuf[i * 4 + 3] = p.W;
                _world3DLightColBuf[i * 4 + 0] = c.X;
                _world3DLightColBuf[i * 4 + 1] = c.Y;
                _world3DLightColBuf[i * 4 + 2] = c.Z;
                _world3DLightColBuf[i * 4 + 3] = c.W;
            }

            program.SetUniform("uLightCount", count);
            if (count == 0)
                return;

            GL.Uniform4(program.GetUniform("uLightPosRadius[0]"), count, _world3DLightPosBuf);
            GL.Uniform4(program.GetUniform("uLightColor[0]"), count, _world3DLightColBuf);
            CheckGlError();
        }

        private void SetPrimitiveRestart(bool enabled)
        {
            if (_hasGLPrimitiveRestart)
            {
                if (enabled) GL.Enable(EnableCap.PrimitiveRestart);
                else GL.Disable(EnableCap.PrimitiveRestart);
            }

            if (_hasGLPrimitiveRestartFixedIndex)
            {
                if (enabled) GL.Enable(EnableCap.PrimitiveRestartFixedIndex);
                else GL.Disable(EnableCap.PrimitiveRestartFixedIndex);
            }

            CheckGlError();
        }

        private void SetMatrix(GLShaderProgram program, string name, in Matrix4x4 m)
        {
            // System.Numerics is row-vector/row-major; uploading it untransposed gives GLSL the
            // column-vector equivalent, so the shader can do `matrix * vec`.
            var b = _world3DMatBuf;
            b[0] = m.M11; b[1] = m.M12; b[2] = m.M13; b[3] = m.M14;
            b[4] = m.M21; b[5] = m.M22; b[6] = m.M23; b[7] = m.M24;
            b[8] = m.M31; b[9] = m.M32; b[10] = m.M33; b[11] = m.M34;
            b[12] = m.M41; b[13] = m.M42; b[14] = m.M43; b[15] = m.M44;
            GL.UniformMatrix4(program.GetUniform(name), 1, false, b);
        }
    }
}

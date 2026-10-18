using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using OpenToolkit.Graphics.OpenGL4;
using Robust.Client.GameObjects;
using Robust.Client.Graphics.World3D;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace Robust.Client.Graphics.Clyde
{
    // Real 3D meshes (Resources/Models3D/<name>.json + .png, made by tools/models in Blender) drawn instead of the
    // sprite-derived model when content's ModelProvider asks for one. Parts are tinted with the entity's own sprite
    // palette ("p0".."p2") or content colours ("c0".."c7"), and can be posed by bone (doors, limbs).
    internal partial class Clyde
    {
        private sealed class World3DMesh
        {
            public ClydeHandle Texture;
            // Keeps the texture alive: an unreferenced OwnedTexture is freed by its finalizer.
            public OwnedTexture? TextureRef;
            public Vector3 Min;
            public Vector3 Max;
            public World3DMeshPart[] Parts = Array.Empty<World3DMeshPart>();
        }

        private struct World3DMeshPart
        {
            public int Start;
            public int Count;
            public char TintKind; // 'p' palette, 'c' content colour, 'n' none
            public int TintIndex;
            public string? Bone;
            public string? Group;
            public Vector3 Pivot;
        }

        // Null entries = failed to load (logged once).
        private readonly Dictionary<string, World3DMesh?> _world3DMeshes = new();

        // Sprite palettes (3 colours, most common first) per front-frame rectangle.
        private readonly Dictionary<World3DModelKey, Vector4[]> _world3DPalettes = new();

        private World3DMesh? GetWorld3DMesh(string name)
        {
            if (_world3DMeshes.TryGetValue(name, out var cached))
                return cached;

            World3DMesh? mesh = null;
            try
            {
                mesh = LoadWorld3DMesh(name);
            }
            catch (Exception e)
            {
                _clydeSawmill.Error($"world3d: failed to load model {name}: {e.Message}");
            }

            _world3DMeshes[name] = mesh;
            return mesh;
        }

        private World3DMesh LoadWorld3DMesh(string name)
        {
            var root = new ResPath("/Models3D");
            using var json = JsonDocument.Parse(_resManager.ContentFileRead(root / $"{name}.json"));
            var doc = json.RootElement;

            var texName = doc.GetProperty("texture").GetString()!;
            using var png = _resManager.ContentFileRead(root / texName);
            var texture = (ClydeTexture) LoadTextureFromPNGStream(png, $"model3d-{name}");
            // Smooth filtering with mipmaps: these are painted textures, not pixel art.
            var loaded = _loadedTextures[texture.TextureId];
            GL.BindTexture(TextureTarget.Texture2D, loaded.OpenGLObject.Handle);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int) TextureMinFilter.LinearMipmapLinear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int) TextureMagFilter.Linear);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            _world3DMipmapped.Add(texture.TextureId);

            var b = doc.GetProperty("bounds");
            var mesh = new World3DMesh
            {
                Texture = texture.TextureId,
                TextureRef = texture,
                Min = new Vector3(b[0].GetSingle(), b[1].GetSingle(), b[2].GetSingle()),
                Max = new Vector3(b[3].GetSingle(), b[4].GetSingle(), b[5].GetSingle()),
            };

            var parts = new List<World3DMeshPart>();
            foreach (var p in doc.GetProperty("parts").EnumerateArray())
            {
                var tint = p.GetProperty("tint").GetString() ?? "none";
                var part = new World3DMeshPart
                {
                    Start = _world3DModelVerts.Count,
                    TintKind = tint.Length == 2 && tint[0] is 'p' or 'c' or 's' && char.IsDigit(tint[1]) ? tint[0] : 'n',
                    TintIndex = tint.Length == 2 && char.IsDigit(tint[1]) ? tint[1] - '0' : 0,
                    Bone = p.TryGetProperty("bone", out var bone) && bone.ValueKind == JsonValueKind.String ? bone.GetString() : null,
                    Group = p.TryGetProperty("group", out var group) && group.ValueKind == JsonValueKind.String ? group.GetString() : null,
                };
                if (p.TryGetProperty("pivot", out var pivot) && pivot.ValueKind == JsonValueKind.Array)
                    part.Pivot = new Vector3(pivot[0].GetSingle(), pivot[1].GetSingle(), pivot[2].GetSingle());

                // x y z nx ny nz u v per vertex, three vertices per triangle.
                var tris = p.GetProperty("tris");
                var n = tris.GetArrayLength() / 8;
                using var it = tris.EnumerateArray();
                Span<float> f = stackalloc float[8];
                for (var v = 0; v < n; v++)
                {
                    for (var k = 0; k < 8; k++)
                    {
                        it.MoveNext();
                        f[k] = it.Current.GetSingle();
                    }

                    _world3DModelVerts.Add(new Vertex3D(new Vector3(f[0], f[1], f[2]), new Vector2(f[6], f[7]),
                        new Vector3(f[3], f[4], f[5]), Vector4.One));
                }

                part.Count = n;
                parts.Add(part);
            }

            mesh.Parts = parts.ToArray();
            _clydeSawmill.Info($"world3d: loaded model {name} ({parts.Count} parts)");
            return mesh;
        }

        /// <summary>Draws <paramref name="request"/>'s mesh for this entity. False if the mesh isn't available.</summary>
        private bool EmitMeshModel(in SpriteData entry, World3DModelRequest request)
        {
            var mesh = GetWorld3DMesh(request.Model);
            if (mesh == null)
                return false;

            // An explicit world yaw faces the model's front (-Y) along it; otherwise it turns with the entity.
            var basePos = request.Position ?? entry.WorldPos;
            var yaw = request.WorldYaw is { } worldYaw ? worldYaw + MathF.PI : (float) entry.WorldRot.Theta + request.Yaw;
            var placement = Matrix4x4.CreateScale(request.Scale)
                            * Matrix4x4.CreateRotationZ(yaw)
                            * Matrix4x4.CreateTranslation(new Vector3(basePos, 0));
            placement *= PoseMatrix(entry.Uid, basePos, out _);

            Vector4[]? palette = null;
            foreach (var part in mesh.Parts)
            {
                if (part.Group != null && request.Hidden.Contains(part.Group))
                    continue;

                var tint = Vector4.One;
                if (part.TintKind == 'c')
                    tint = EntityMainColor(request.ColorEntities[part.TintIndex])
                           ?? LayerMainColor(entry.Sprite, request.ColorLayers[part.TintIndex])
                           ?? request.Colors[part.TintIndex];
                else if (part.TintKind == 'p')
                    tint = (palette ??= SpritePalette(entry.Sprite))[Math.Min(part.TintIndex, 2)];

                var model = BoneMatrix(request, part) * placement;

                if (part.TintKind == 's')
                {
                    EmitDecalPart(entry, request, part, model);
                    continue;
                }

                _world3DInstances.Add(new World3DInstance
                {
                    Texture = mesh.Texture, Start = part.Start, Count = part.Count, Model = model, Tint = tint,
                });
            }

            if (request.NoPick)
                return true;

            // Click volume: the mesh bounds, turned with the entity.
            var rot = entry.WorldRot + new Angle(request.Yaw);
            var size = (mesh.Max - mesh.Min) * request.Scale / 2f;
            var centre = (mesh.Min + mesh.Max) * request.Scale / 2f;
            var z = PoseZ(entry.Uid);
            AddPick(entry.Uid, entry.WorldPos + rot.RotateVec(new Vector2(centre.X, centre.Y)),
                rot.RotateVec(new Vector2(MathF.Max(size.X, 0.15f), 0)), rot.RotateVec(new Vector2(0, MathF.Max(size.Y, 0.15f))),
                z + mesh.Min.Z * request.Scale, z + mesh.Max.Z * request.Scale);
            return true;
        }

        /// <summary>
        ///     The sprite's three main colours (most pixels first), from its first drawn layer's front frame, ignoring
        ///     transparent pixels and the dark outline. Shades of one colour count as one.
        /// </summary>
        private Vector4[] SpritePalette(SpriteComponent sprite)
        {
            foreach (var layer in sprite.Layers)
            {
                if (FrontFrame(sprite, layer) is not { } frame)
                    continue;

                var ct = RenderHandle.ExtractTexture(frame, null, out var sr);
                var left = (int) MathF.Round(sr.Left);
                var top = (int) MathF.Round(sr.Top);
                var right = (int) MathF.Round(sr.Right);
                var bottom = (int) MathF.Round(sr.Bottom);
                var key = new World3DModelKey(ct.TextureId, left, top, right, bottom, -2);
                if (_world3DPalettes.TryGetValue(key, out var cachedPalette))
                    return Tinted(cachedPalette, LayerColor(sprite, layer));

                var w = right - left;
                var h = bottom - top;
                var rgba = GetRegionRgba(ct, left, top, w, h);
                var palette = ComputePalette(rgba, w, h);
                _world3DPalettes[key] = palette;
                return Tinted(palette, LayerColor(sprite, layer));
            }

            return new[] { Vector4.One, new Vector4(0.8f, 0.8f, 0.8f, 1), new Vector4(0.5f, 0.5f, 0.5f, 1) };
        }

        /// <summary>The part's bone pose (rotation about its pivot) from the request, or identity.</summary>
        private static Matrix4x4 BoneMatrix(World3DModelRequest request, in World3DMeshPart part)
        {
            if (part.Bone == null || !request.Bones.TryGetValue(part.Bone, out var euler) || euler == Vector3.Zero)
                return Matrix4x4.Identity;

            return Matrix4x4.CreateTranslation(-part.Pivot)
                   * Matrix4x4.CreateRotationX(euler.X)
                   * Matrix4x4.CreateRotationY(euler.Y)
                   * Matrix4x4.CreateRotationZ(euler.Z)
                   * Matrix4x4.CreateTranslation(part.Pivot);
        }

        /// <summary>
        ///     A decal part (tint "s0".."s7") shows the entity's own sprite on a model face that faces -Y: "s0" stacks
        ///     every drawn layer of the front frame (so animated screens and lights keep working), "sN" shows only the
        ///     layer content named in <see cref="World3DModelRequest.ColorLayers"/>[N].
        /// </summary>
        private void EmitDecalPart(in SpriteData entry, World3DModelRequest request, in World3DMeshPart part, in Matrix4x4 model)
        {
            var sprite = entry.Sprite;
            if (part.TintIndex == 0)
            {
                var n = 0;
                (float L, float T, float R, float B)? crop = null;
                foreach (var layer in sprite.Layers)
                {
                    if (!layer.Visible || GetLayerTexture(sprite, layer, Angle.Zero) is not { } frame)
                        continue;

                    // Every layer uses the base layer's crop so overlays (screens, lights) stay aligned with it.
                    crop ??= FrameCrop(frame);
                    // Later layers sit a hair in front so they never z-fight with the ones below.
                    var lift = Matrix4x4.CreateTranslation(0, -0.0015f * n++, 0) * model;
                    AddDecalInstance(frame, part, lift, LayerColor(sprite, layer), crop.Value);
                }

                return;
            }

            if (request.ColorLayers[part.TintIndex] is { } key
                && sprite.LayerMap.TryGetValue(key, out var index)
                && index < sprite.Layers.Count)
            {
                var layer = sprite.Layers[index];
                if (layer.ActualState == null && index + 1 < sprite.Layers.Count)
                    layer = sprite.Layers[index + 1];
                if (layer.Visible && GetLayerTexture(sprite, layer, Angle.Zero) is { } frame)
                    AddDecalInstance(frame, part, model, LayerColor(sprite, layer), FrameCrop(frame));
            }
        }

        /// <summary>Frame-local crop (pixels from the frame's left/top) around its opaque pixels.</summary>
        private (float L, float T, float R, float B) FrameCrop(Texture frame)
        {
            var ct = RenderHandle.ExtractTexture(frame, null, out var sr);
            var (l, t, r, b) = OpaqueBounds(ct, sr);
            return (l - sr.Left, t - sr.Top, r - sr.Left, b - sr.Top);
        }

        private void AddDecalInstance(Texture frame, in World3DMeshPart part, in Matrix4x4 model, Vector4 tint,
            (float L, float T, float R, float B) crop)
        {
            var ct = RenderHandle.ExtractTexture(frame, null, out var sr);
            var loaded = _loadedTextures[ct.TextureId];
            float w = loaded.Width, h = loaded.Height;
            // Crop to the opaque pixels so the art fills the model face instead of keeping the frame margins.
            float cl = sr.Left + crop.L, ct2 = sr.Top + crop.T, cr = sr.Left + crop.R, cb = sr.Top + crop.B;
            // Sprite atlases map pixel row py to v = (H - py) / H (see BuildExtrusion).
            var rect = new Vector4(cl / w, (h - cb) / h, (cr - cl) / w, (cb - ct2) / h);
            _world3DInstances.Add(new World3DInstance
            {
                Texture = ct.TextureId, Start = part.Start, Count = part.Count, Model = model, Tint = tint, UVRect = rect,
            });
        }

        private readonly Dictionary<World3DModelKey, (float L, float T, float R, float B)> _world3DOpaqueBounds = new();

        /// <summary>Atlas pixel rectangle around the opaque pixels of a sprite frame (the whole frame if it's empty).</summary>
        private (float L, float T, float R, float B) OpaqueBounds(ClydeTexture ct, UIBox2 sr)
        {
            var left = (int) MathF.Round(sr.Left);
            var top = (int) MathF.Round(sr.Top);
            var right = (int) MathF.Round(sr.Right);
            var bottom = (int) MathF.Round(sr.Bottom);
            var key = new World3DModelKey(ct.TextureId, left, top, right, bottom, -3);
            if (_world3DOpaqueBounds.TryGetValue(key, out var cached))
                return cached;

            var w = right - left;
            var h = bottom - top;
            var rgba = GetRegionRgba(ct, left, top, w, h);
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                if (rgba[(y * w + x) * 4 + 3] < AlphaSolid)
                    continue;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }

            var result = maxX < 0
                ? (left, top, right, bottom)
                : (left + minX, top + minY, left + maxX + 1f, top + maxY + 1f);
            _world3DOpaqueBounds[key] = result;
            return result;
        }

        /// <summary>Main colour of another entity's sprite (e.g. a worn item's icon).</summary>
        private Vector4? EntityMainColor(EntityUid? uid)
        {
            if (uid is not { } ent || !_entityManager.TryGetComponent(ent, out SpriteComponent? sprite))
                return null;

            return SpritePalette(sprite)[0];
        }

        /// <summary>Main colour of the sprite layer mapped to <paramref name="key"/> (as drawn facing south), if it's visible.</summary>
        private Vector4? LayerMainColor(SpriteComponent sprite, object? key)
        {
            if (key == null || !sprite.LayerMap.TryGetValue(key, out var index) || index >= sprite.Layers.Count)
                return null;

            // Slot keys ("jumpsuit", hair...) are often empty bookmarks with the real layer inserted right after them.
            var layer = sprite.Layers[index];
            if (layer.ActualState == null && index + 1 < sprite.Layers.Count)
                layer = sprite.Layers[index + 1];
            if (!layer.Visible || GetLayerTexture(sprite, layer, Angle.Zero) is not { } frame)
                return null;

            var ct = RenderHandle.ExtractTexture(frame, null, out var sr);
            var left = (int) MathF.Round(sr.Left);
            var top = (int) MathF.Round(sr.Top);
            var right = (int) MathF.Round(sr.Right);
            var bottom = (int) MathF.Round(sr.Bottom);
            var rkey = new World3DModelKey(ct.TextureId, left, top, right, bottom, -2);
            if (!_world3DPalettes.TryGetValue(rkey, out var palette))
            {
                palette = ComputePalette(GetRegionRgba(ct, left, top, right - left, bottom - top), right - left, bottom - top);
                _world3DPalettes[rkey] = palette;
            }

            return palette[0] * LayerColor(sprite, layer);
        }

        private static Vector4[] Tinted(Vector4[] palette, Vector4 color)
            => new[] { palette[0] * color, palette[1] * color, palette[2] * color };

        private static Vector4[] ComputePalette(byte[] rgba, int w, int h)
        {
            // Bucket by hue/saturation family (coarse RGB with brightness normalised), weighted by pixel count;
            // each bucket keeps its average colour.
            var buckets = new Dictionary<int, (Vector3 Sum, int Count)>();
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                if (rgba[i + 3] < AlphaSolid || IsOutline(rgba, w, h, x, y))
                    continue;

                var c = new Vector3(rgba[i], rgba[i + 1], rgba[i + 2]) / 255f;
                var max = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
                var lum = c.X * 0.3f + c.Y * 0.59f + c.Z * 0.11f;
                if (lum < 0.06f)
                    continue;
                var chroma = max > 0 ? c / max : c;
                var key = ((int) (chroma.X * 4.99f) * 25 + (int) (chroma.Y * 4.99f) * 5 + (int) (chroma.Z * 4.99f)) * 4
                          + (int) (MathF.Min(lum, 0.999f) * 4f); // a few brightness bands
                buckets.TryGetValue(key, out var b);
                buckets[key] = (b.Sum + c, b.Count + 1);
            }

            var ranked = new List<(Vector3 Color, int Count)>();
            foreach (var (_, b) in buckets)
                ranked.Add((b.Sum / b.Count, b.Count));
            ranked.Sort((a, b) => b.Count.CompareTo(a.Count));

            var result = new List<Vector4>();
            foreach (var (color, _) in ranked)
            {
                var distinct = true;
                foreach (var chosen in result)
                {
                    if ((new Vector3(chosen.X, chosen.Y, chosen.Z) - color).Length() < 0.18f)
                        distinct = false;
                }

                if (distinct)
                    result.Add(new Vector4(color, 1));
                if (result.Count == 3)
                    break;
            }

            if (result.Count == 0)
                result.Add(new Vector4(0.7f, 0.7f, 0.7f, 1));
            while (result.Count < 3)
                result.Add(result[^1] * new Vector4(0.75f, 0.75f, 0.75f, 1));
            return result.ToArray();
        }

        private static bool IsOutline(byte[] rgba, int w, int h, int x, int y)
        {
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var nx = x + dx;
                var ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h || rgba[(ny * w + nx) * 4 + 3] < AlphaSolid)
                    return true;
            }

            return false;
        }
    }
}

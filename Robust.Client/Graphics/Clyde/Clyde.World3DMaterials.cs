using System;
using System.Collections.Generic;
using System.Numerics;
using OpenToolkit.Graphics.OpenGL4;
using Robust.Client.GameObjects;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Maths;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Robust.Shared.Utility;

namespace Robust.Client.Graphics.Clyde
{
    // Cohesive surface materials for the 3D view.
    // SS14's wall art is top-down corner fragments (solid0..7), which looks like noise when stretched up a wall.
    // Instead every wall type gets a generated 32×32 (1 m) pixel-art panel in the colours of its own icon ("full"
    // state): seams, a bevel, rivets and a light dither, so all walls share one style. Windows get a glass variant
    // and every ceiling shares one panel. All 3D-drawn textures also get mipmaps so distant surfaces don't shimmer.
    internal partial class Clyde
    {
        private const int MaterialSize = 32;

        private readonly Dictionary<World3DModelKey, (ClydeTexture Texture, bool Glass, Vector4 Color)> _world3DWallMaterials = new();
        private ClydeTexture? _world3DCeilingMaterial;
        private readonly HashSet<ClydeHandle> _world3DMipmapped = new();

        /// <summary>The generated wall panel for a wall/window sprite, and whether it's see-through.</summary>
        private (ClydeTexture Texture, bool Glass, Vector4 Color)? GetWallMaterial(SpriteComponent sprite)
        {
            // Prefer the RSI's "full" icon (the whole wall); fall back to whatever the first layer shows.
            Texture? source = null;
            foreach (var layer in sprite.Layers)
            {
                if (layer.ActualRsi is { } rsi && rsi.TryGetState("full", out var full))
                {
                    source = full.GetFrame(RsiDirection.South, 0);
                    break;
                }
            }

            if (source == null)
            {
                foreach (var layer in sprite.Layers)
                {
                    if (FrontFrame(sprite, layer) is { } frame)
                    {
                        source = frame;
                        break;
                    }
                }
            }

            if (source == null)
                return null;

            var ct = RenderHandle.ExtractTexture(source, null, out var sr);
            var left = (int) MathF.Round(sr.Left);
            var top = (int) MathF.Round(sr.Top);
            var w = (int) MathF.Round(sr.Width);
            var h = (int) MathF.Round(sr.Height);
            var key = new World3DModelKey(ct.TextureId, left, top, left + w, top + h, -2);
            if (_world3DWallMaterials.TryGetValue(key, out var cached))
                return cached;

            var pixels = GetRegionRgba(ct, left, top, w, h);
            var (baseColor, coverage) = AverageColor(pixels);
            var glass = coverage < 0.75f;
            var texture = MakePanelTexture(baseColor, glass, rivets: !glass, $"world3d-wall-{key.Left}-{key.Top}");
            _world3DWallMaterials[key] = (texture, glass, baseColor);
            return (texture, glass, baseColor);
        }

        // Stylized material library (Resources/Models3D/Materials, made by Tools/models3d/materials.py): 256 px per metre,
        // packed into one atlas with 16 px of wrapped padding per cell so each tiles seamlessly and mips don't bleed.
        private const int MatCell = 256;
        private const int MatPad = 16;
        private ClydeTexture? _world3DMatAtlas;
        private readonly Dictionary<string, (Vector2 TL, Vector2 BR)> _world3DMatRegions = new();
        private bool _world3DMatLoaded;

        /// <summary>Atlas texture and UV rectangle (ResolveUV convention) of a library material.</summary>
        private bool TryLibraryMaterial(string? name, out ClydeTexture atlas, out Vector2 uvTL, out Vector2 uvBR)
        {
            EnsureMaterialAtlas();
            atlas = _world3DMatAtlas!;
            uvTL = uvBR = default;
            if (name == null || _world3DMatAtlas == null || !_world3DMatRegions.TryGetValue(name, out var r))
                return false;

            (uvTL, uvBR) = r;
            return true;
        }

        private void EnsureMaterialAtlas()
        {
            if (_world3DMatLoaded)
                return;
            _world3DMatLoaded = true;

            var files = new List<ResPath>();
            foreach (var path in _resManager.ContentFindFiles(new ResPath("/Models3D/Materials/")))
            {
                if (path.Extension == "png")
                    files.Add(path);
            }

            if (files.Count == 0)
                return;

            files.Sort((a, b) => string.CompareOrdinal(a.ToString(), b.ToString()));
            const int cols = 8;
            var rows = (files.Count + cols - 1) / cols;
            const int stride = MatCell + 2 * MatPad;
            using var atlas = new Image<Rgba32>(cols * stride, rows * stride);
            float aw = atlas.Width, ah = atlas.Height;

            for (var i = 0; i < files.Count; i++)
            {
                using var stream = _resManager.ContentFileRead(files[i]);
                using var img = Image.Load<Rgba32>(stream);
                var ox = i % cols * stride;
                var oy = i / cols * stride;
                for (var y = -MatPad; y < MatCell + MatPad; y++)
                for (var x = -MatPad; x < MatCell + MatPad; x++)
                {
                    var sx = ((x % img.Width) + img.Width) % img.Width;
                    var sy = ((y % img.Height) + img.Height) % img.Height;
                    atlas[ox + MatPad + x, oy + MatPad + y] = img[sx, sy];
                }

                float l = ox + MatPad, t = oy + MatPad, r = l + MatCell, b = t + MatCell;
                // Same convention as ResolveUV: image row t maps to v = (H - t) / H.
                _world3DMatRegions[files[i].FilenameWithoutExtension] =
                    (new Vector2(l / aw, (ah - t) / ah), new Vector2(r / aw, (ah - b) / ah));
            }

            _world3DMatAtlas = (ClydeTexture) LoadTextureFromImage(atlas, "world3d-materials");
            var loaded = _loadedTextures[_world3DMatAtlas.TextureId];
            GL.BindTexture(TextureTarget.Texture2D, loaded.OpenGLObject.Handle);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 4);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int) TextureMinFilter.LinearMipmapLinear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int) TextureMagFilter.Linear);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            _world3DMipmapped.Add(_world3DMatAtlas.TextureId);
            CheckGlError();
            _clydeSawmill.Info($"world3d: material library: {files.Count} materials");
        }

        private readonly Dictionary<(int Type, byte Variant), Vector4> _world3DTileTints = new();

        /// <summary>A floor tile's tint for its stylized material: the average colour of its own pixel art.</summary>
        private Vector4 TileTint(Robust.Shared.Map.Tile tile, Box2 region)
        {
            if (_world3DTileTints.TryGetValue((tile.TypeId, tile.Variant), out var cached))
                return cached;

            var atlas = (ClydeTexture) _tileDefinitionManager.TileTextureAtlas;
            var loaded = _loadedTextures[atlas.TextureId];
            float w = loaded.Width, h = loaded.Height;
            // Regions are UVs with v = (H - row) / H; convert back to an image rectangle.
            var vMin = MathF.Min(region.Bottom, region.Top);
            var vMax = MathF.Max(region.Bottom, region.Top);
            var left = (int) MathF.Round(region.Left * w);
            var right = (int) MathF.Round(region.Right * w);
            var top = (int) MathF.Round((1 - vMax) * h);
            var bottom = (int) MathF.Round((1 - vMin) * h);
            var tint = new Vector4(0.6f, 0.6f, 0.62f, 1f);
            if (right > left && bottom > top)
            {
                var (avg, _) = AverageColor(GetRegionRgba(atlas, left, top, right - left, bottom - top));
                tint = SurfaceTint(avg);
            }

            _world3DTileTints[(tile.TypeId, tile.Variant)] = tint;
            return tint;
        }

        /// <summary>
        ///     Tint for a library material from an SS14 colour: keep its hue and saturation, lift dark colours so the
        ///     already-shaded material doesn't go muddy.
        /// </summary>
        private static Vector4 SurfaceTint(Vector4 color)
        {
            var max = MathF.Max(color.X, MathF.Max(color.Y, color.Z));
            if (max < 0.001f)
                return new Vector4(0.3f, 0.3f, 0.3f, 1f);
            var target = Math.Clamp(max * 1.45f, 0.32f, 1f);
            return new Vector4(color.X / max * target, color.Y / max * target, color.Z / max * target, 1f);
        }

        /// <summary>One shared ceiling panel for every room.</summary>
        private ClydeTexture GetCeilingMaterial()
        {
            return _world3DCeilingMaterial ??= MakePanelTexture(new Vector4(0.36f, 0.38f, 0.42f, 1f), glass: false,
                rivets: false, "world3d-ceiling");
        }

        /// <summary>
        ///     Average colour of the visible pixels (sRGB 0..1) and how opaque they are on average
        ///     (low for glass, ~1 for solid walls).
        /// </summary>
        private static (Vector4 Color, float Coverage) AverageColor(byte[] rgba)
        {
            double r = 0, g = 0, b = 0, weight = 0, alphaSum = 0;
            var visible = 0;
            for (var i = 0; i < rgba.Length; i += 4)
            {
                var a = rgba[i + 3];
                if (a < 16)
                    continue;

                // Skip the near-black outline so it doesn't drag every wall towards black.
                var lum = (rgba[i] + rgba[i + 1] + rgba[i + 2]) / 3.0;
                var wgt = a / 255.0 * (lum < 30 ? 0.15 : 1.0);
                r += rgba[i] * wgt;
                g += rgba[i + 1] * wgt;
                b += rgba[i + 2] * wgt;
                weight += wgt;
                alphaSum += a / 255.0;
                visible++;
            }

            if (weight <= 0 || visible == 0)
                return (new Vector4(0.45f, 0.47f, 0.5f, 1f), 1f);

            return (new Vector4((float) (r / weight / 255), (float) (g / weight / 255), (float) (b / weight / 255), 1f),
                (float) (alphaSum / visible));
        }

        /// <summary>
        ///     A 1 m wall/ceiling panel in pixel-art style: seams on the edges, a bevel highlight, a slightly darker
        ///     belt, optional corner rivets, and a deterministic dither so large surfaces don't look flat.
        /// </summary>
        private ClydeTexture MakePanelTexture(Vector4 baseColor, bool glass, bool rivets, string name)
        {
            var image = new Image<Rgba32>(MaterialSize, MaterialSize);
            const int last = MaterialSize - 1;

            for (var y = 0; y < MaterialSize; y++)
            {
                for (var x = 0; x < MaterialSize; x++)
                {
                    var shade = 1f;
                    var alpha = 1f;

                    if (y == 0 || x == 0) shade = 0.55f;              // seam
                    else if (y == last || x == last) shade = 0.72f;   // shadowed edge
                    else if (y == 1 || x == 1) shade = 1.18f;          // bevel highlight
                    else if (y is >= 14 and <= 17) shade = y is 14 or 17 ? 0.8f : 0.9f; // belt

                    if (rivets && (x is 4 or 27) && (y is 4 or 27))
                        shade = 1.3f;
                    else if (rivets && (x is 5 or 28) && (y is 5 or 28))
                        shade = 0.7f;

                    if (glass)
                    {
                        // Opaque frame, see-through pane with a faint diagonal glint.
                        var frame = x <= 1 || y <= 1 || x >= last - 1 || y >= last - 1;
                        alpha = frame ? 1f : 0.35f;
                        if (!frame && (x + y) % 23 is 0 or 1)
                        {
                            shade = 1.5f;
                            alpha = 0.5f;
                        }
                    }

                    // Deterministic ±4% dither.
                    var hash = (uint) (x * 73856093 ^ y * 19349663 ^ name.Length * 83492791);
                    shade *= 0.96f + (hash % 9) / 100f;

                    image[x, y] = new Rgba32(
                        Math.Clamp(baseColor.X * shade, 0f, 1f),
                        Math.Clamp(baseColor.Y * shade, 0f, 1f),
                        Math.Clamp(baseColor.Z * shade, 0f, 1f),
                        alpha);
                }
            }

            var texture = (ClydeTexture) LoadTextureFromImage(image, name);
            EnsureMipmaps(texture.TextureId, maxLevel: 5);
            return texture;
        }

        /// <summary>
        ///     Gives a texture mipmaps (once) so it doesn't shimmer when minified in the 3D view. Atlases are limited
        ///     to a few levels so neighbouring sprites don't bleed into each other.
        /// </summary>
        private void EnsureMipmaps(ClydeHandle handle, int maxLevel = 3)
        {
            if (!_world3DMipmapped.Add(handle) || !_loadedTextures.TryGetValue(handle, out var loaded))
                return;

            GL.BindTexture(TextureTarget.Texture2D, loaded.OpenGLObject.Handle);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, maxLevel);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int) TextureMinFilter.NearestMipmapLinear);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            CheckGlError();
        }
    }
}

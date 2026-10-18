using System;
using System.Collections.Generic;
using System.Numerics;
using OpenToolkit.Graphics.OpenGL4;
using Robust.Client.GameObjects;
using Robust.Client.Graphics.World3D;
using Robust.Shared.Maths;

namespace Robust.Client.Graphics.Clyde
{
    // Stage 5 visuals: the first-person view model (active-hand item at the lower right, GMod-style) and beams.
    internal partial class Clyde
    {
        // View model placement relative to the camera, metres.
        private const float ViewModelForward = 0.5f;
        private const float ViewModelRight = 0.2f;
        private const float ViewModelDown = 0.2f;
        private const float ViewModelScale = 0.45f;

        private readonly List<World3DInstance> _world3DViewModel = new();
        private Vector3 _world3DMuzzle;

        /// <summary>Camera basis for the current frame (forward, right, up).</summary>
        private (Vector3 Fwd, Vector3 Right, Vector3 Up) CameraBasis()
        {
            var fwd = _world3D!.Forward;
            var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitZ));
            if (!float.IsFinite(right.X))
                right = Vector3.UnitX;
            var up = Vector3.Cross(right, fwd);
            return (fwd, right, up);
        }

        /// <summary>
        ///     Builds the view model instances for this frame and the muzzle point beams start from.
        ///     The item's front sprite frame is extruded thin and held pointing into the screen.
        /// </summary>
        private void BuildWorld3DViewModel(Vector3 camPos)
        {
            _world3DViewModel.Clear();
            var (fwd, right, up) = CameraBasis();
            var anchor = camPos + fwd * ViewModelForward + right * ViewModelRight - up * ViewModelDown;
            _world3DMuzzle = anchor + fwd * (ViewModelScale * 0.5f);

            if (_world3D!.ViewModelEntity is not { } uid
                || !_entityManager.TryGetComponent(uid, out SpriteComponent? sprite))
                return;

            // Image right → forward (guns point right in SS14 icons), extrusion → sideways, image up → up.
            var basis = new Matrix4x4(
                fwd.X, fwd.Y, fwd.Z, 0,
                right.X, right.Y, right.Z, 0,
                up.X, up.Y, up.Z, 0,
                anchor.X, anchor.Y, anchor.Z, 1);
            var placement = Matrix4x4.CreateScale(ViewModelScale) * basis;

            for (var l = 0; l < sprite.Layers.Count; l++)
            {
                var layer = sprite.Layers[l];
                var frame = GetLayerTexture(sprite, layer, Angle.Zero);
                if (frame == null)
                    continue;

                var (tex, start, count, height) = GetExtrudedModel(frame, 0.08f + 0.01f * l);
                var model = Matrix4x4.CreateTranslation(0, 0, -height / 2f) * placement;
                _world3DViewModel.Add(new World3DInstance
                {
                    Texture = tex, Start = start, Count = count, Model = model, Tint = LayerColor(sprite, layer),
                });
            }
        }

        /// <summary>Glowing camera-facing strips for every beam, pushed into the dynamic buffer.</summary>
        private void EmitWorld3DBeams(Vector3 camPos)
        {
            var white = ((ClydeTexture) _stockTextureWhite).TextureId;
            foreach (var beam in _world3D!.Beams)
            {
                var start = beam.FromViewModel ? _world3DMuzzle : beam.Start;
                var end = beam.End;
                var dir = end - start;
                if (dir.LengthSquared() < 1e-6f)
                    continue;

                // Lights barely reach the shader's range for HDR, so overdrive the colour to read as a glow.
                var c = Color.FromSrgb(beam.Color);
                var glow = new Vector4(c.R * 4f, c.G * 4f, c.B * 4f, c.A);

                // Two strips: a thin bright core and a wider faint halo.
                PushBeamStrip(white, start, end, camPos, 0.012f, 0.03f, glow);
                PushBeamStrip(white, start, end, camPos, 0.035f, 0.08f, glow * new Vector4(0.6f, 0.6f, 0.6f, 0.35f));
            }
        }

        private void PushBeamStrip(ClydeHandle tex, Vector3 start, Vector3 end, Vector3 camPos, float startWidth,
            float endWidth, Vector4 color)
        {
            var dir = Vector3.Normalize(end - start);
            var mid = (start + end) / 2f;
            var side = Vector3.Cross(dir, Vector3.Normalize(camPos - mid));
            if (side.LengthSquared() < 1e-8f)
                side = Vector3.Cross(dir, Vector3.UnitZ);
            side = Vector3.Normalize(side);
            var normal = Vector3.Normalize(camPos - mid);
            var uv = new Vector2(0.5f, 0.5f);

            PushQuad(tex,
                start - side * startWidth, start + side * startWidth, end + side * endWidth, end - side * endWidth,
                uv, uv, uv, uv, normal, color);
        }

        /// <summary>Draws the view model on top of the world (depth cleared first so it never clips into walls).</summary>
        private void DrawWorld3DViewModel(GLShaderProgram program)
        {
            if (_world3DViewModel.Count == 0)
                return;

            GL.DepthMask(true);
            GL.Clear(ClearBufferMask.DepthBufferBit);
            IsBlending = false;
            program.SetUniform("uPass", 0);

            BindVertexArray(_world3DModelVao);
            foreach (var inst in _world3DViewModel)
            {
                SetTexture(TextureUnit.Texture0, inst.Texture);
                SetMatrix(program, "uModel", inst.Model);
                program.SetUniform("uTint", inst.Tint);
                GL.DrawArrays(PrimitiveType.Triangles, inst.Start, inst.Count);
                _debugStats.LastGLDrawCalls += 1;
            }

            program.SetUniform("uTint", Vector4.One);
            CheckGlError();
        }
    }
}

using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.Graphics.World3D;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Shared.Enums;

namespace Content.Client.Fps3D;

/// <summary>
///     GMod-style crosshair for the first-person view, plus the name of whatever it is on (like GMod's target ID).
/// </summary>
public sealed class Fps3DCrosshairOverlay : Overlay
{
    private const float InteractRange = 1.5f; // SharedInteractionSystem.InteractionRange
    private const float NameRange = 6f;

    private readonly IEntityManager _entities;
    private readonly IEyeManager _eye;
    private readonly IUserInterfaceManager _ui;
    private readonly IWorld3DManager _world3D;
    private readonly Font _font;

    public override OverlaySpace Space => OverlaySpace.ScreenSpace;

    public bool Enabled;

    public Fps3DCrosshairOverlay(IEntityManager entities, IEyeManager eye, IUserInterfaceManager ui, IWorld3DManager world3D, IResourceCache cache)
    {
        _font = new VectorFont(cache.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Bold.ttf"), 11);
        _entities = entities;
        _eye = eye;
        _ui = ui;
        _world3D = world3D;
    }

    protected override bool BeforeDraw(in OverlayDrawArgs args) => Enabled && _world3D.CrosshairMode;

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (_eye.MainViewport is not Control viewport)
            return;

        var handle = args.ScreenHandle;
        var center = viewport.GlobalPixelPosition + viewport.PixelSize / 2;
        var c = new Vector2(center.X, center.Y);
        var scale = MathF.Max(1f, _ui.DefaultUIScale);

        var target = _world3D.CrosshairEntity;
        var inReach = target != null && _world3D.CrosshairDistance <= InteractRange + 0.6f; // camera sits ~0.6 m from the body's reach origin
        var color = inReach ? Color.White : Color.White.WithAlpha(0.75f);

        // Small "+" with a dark outline so it reads on any background.
        var arm = 6f * scale;
        var thick = 1f * scale;
        var outline = Color.Black.WithAlpha(0.6f);
        DrawCross(handle, c, arm + 1, thick + 1, outline);
        DrawCross(handle, c, arm, thick, color);

        if (target is not { } uid || _world3D.CrosshairDistance > NameRange
            || !_entities.TryGetComponent(uid, out MetaDataComponent? meta))
            return;

        var font = _font;
        var name = meta.EntityName;
        var size = handle.GetDimensions(font, name, scale);
        var pos = c + new Vector2(-size.X / 2f, 14f * scale);
        handle.DrawString(font, pos + new Vector2(1, 1), name, scale, Color.Black.WithAlpha(0.7f));
        handle.DrawString(font, pos, name, scale, inReach ? Color.White : Color.LightGray);
    }

    private static void DrawCross(DrawingHandleScreen handle, Vector2 c, float arm, float thick, Color color)
    {
        handle.DrawRect(new UIBox2(c.X - arm, c.Y - thick, c.X + arm, c.Y + thick), color);
        handle.DrawRect(new UIBox2(c.X - thick, c.Y - arm, c.X + thick, c.Y + arm), color);
    }
}

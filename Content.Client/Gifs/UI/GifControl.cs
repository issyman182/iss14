// iss14: GIFs in chat via GifSnap
using System.Numerics;
using Content.Shared.CCVar;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Configuration;
using Robust.Shared.Timing;

namespace Content.Client.Gifs.UI;

/// <summary>
/// Inline chat control that animates a GIF from <see cref="GifClientSystem"/>. It is always exactly the fixed box
/// given by <c>gifs.frame_width</c> x <c>gifs.frame_height</c> (so chat lines can be laid out before the sheet
/// arrives; see <see cref="BoxSize"/>), with the frame drawn fitted and centred inside it. Shows a placeholder panel
/// with the title until the sprite sheet arrives (and keeps showing it if it never does).
/// </summary>
public sealed class GifControl : Control
{
    private static readonly Color PlaceholderBackground = Color.FromHex("#1f2130");
    private static readonly Color PlaceholderBorder = Color.FromHex("#3c3f58");
    private static readonly Color LetterboxBackground = Color.FromHex("#14151e").WithAlpha(0.35f);

    private readonly string _id;
    private readonly GifClientSystem _gifs;
    private readonly IGameTiming _timing;
    private readonly Label _placeholderLabel;

    private GifClientSystem.GifEntry? _entry;

    /// <summary>Rectangle (in this control's pixel space) the current frame is drawn into; recomputed on resize.</summary>
    private UIBox2 _frameRect;

    /// <summary>The fixed box (UI units) every GIF occupies in chat, from the replicated CVars.</summary>
    public static Vector2 BoxSize(IConfigurationManager cfg)
    {
        var w = Math.Clamp(cfg.GetCVar(CCVars.GifsFrameWidth), 32, 1024);
        var h = Math.Clamp(cfg.GetCVar(CCVars.GifsFrameHeight), 32, 1024);
        return new Vector2(w, h);
    }

    public GifControl(string id, string title, GifClientSystem gifs, IGameTiming timing, IConfigurationManager cfg)
    {
        _id = id;
        _gifs = gifs;
        _timing = timing;

        MouseFilter = MouseFilterMode.Stop;
        ToolTip = title;
        VerticalAlignment = VAlignment.Top;
        RectClipContent = true;

        _placeholderLabel = new Label
        {
            Text = Loc.GetString("gifs-loading", ("title", title)),
            HorizontalAlignment = HAlignment.Center,
            VerticalAlignment = VAlignment.Center,
            Margin = new Thickness(6, 2),
            ClipText = true,
        };
        AddChild(_placeholderLabel);

        SetSize = BoxSize(cfg);

        if (_gifs.TryGet(id, out var entry))
            Apply(entry);
        else
            _gifs.Request(id);
    }

    protected override void EnteredTree()
    {
        base.EnteredTree();

        if (_entry != null)
            return;

        // The sheet may have arrived while we were out of the tree.
        if (_gifs.TryGet(_id, out var entry))
            Apply(entry);
        else
            _gifs.GifLoaded += OnGifLoaded;
    }

    protected override void ExitedTree()
    {
        base.ExitedTree();
        _gifs.GifLoaded -= OnGifLoaded;
    }

    private void OnGifLoaded(string id)
    {
        if (id != _id || !_gifs.TryGet(id, out var entry))
            return;

        _gifs.GifLoaded -= OnGifLoaded;
        Apply(entry);
    }

    private void Apply(GifClientSystem.GifEntry entry)
    {
        _entry = entry;
        _placeholderLabel.Visible = false;
        UpdateFrameRect();
    }

    protected override void Resized()
    {
        base.Resized();
        UpdateFrameRect();
    }

    /// <summary>Fits the frame inside the box (keep aspect, never upscale past the frame's own size), centred.</summary>
    private void UpdateFrameRect()
    {
        if (_entry == null)
            return;

        var box = PixelSizeBox;
        var scale = Math.Min(UIScale, Math.Min(box.Width / _entry.FrameWidth, box.Height / _entry.FrameHeight));
        var w = MathF.Round(_entry.FrameWidth * scale);
        var h = MathF.Round(_entry.FrameHeight * scale);
        var x = MathF.Round((box.Width - w) / 2f);
        var y = MathF.Round((box.Height - h) / 2f);
        _frameRect = new UIBox2(x, y, x + w, y + h);
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);

        var box = PixelSizeBox;
        if (_entry == null)
        {
            handle.DrawRect(box, PlaceholderBackground);
            handle.DrawRect(box, PlaceholderBorder, filled: false);
            return;
        }

        if (_frameRect.Width < box.Width || _frameRect.Height < box.Height)
            handle.DrawRect(box, LetterboxBackground);

        var frame = GifClientSystem.FrameAt(_entry, _timing.RealTime);
        handle.DrawTextureRectRegion(_entry.Sheet, _frameRect, _entry.FrameRegions[frame]);
    }
}

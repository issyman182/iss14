// iss14: GIFs in chat via GifSnap
using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Timing;

namespace Content.Client.Gifs.UI;

/// <summary>
/// Inline chat control that animates a GIF from <see cref="GifClientSystem"/>. Shows a placeholder box with the
/// title until the sprite sheet arrives (and keeps showing it if it never does).
/// </summary>
public sealed class GifControl : Control
{
    /// <summary>Widest a GIF may render in chat, in virtual pixels.</summary>
    public const float MaxDisplayWidth = 200f;

    private const float PlaceholderWidth = 120f;
    private const float PlaceholderHeight = 40f;

    private static readonly Color PlaceholderBackground = Color.FromHex("#1f2130");
    private static readonly Color PlaceholderBorder = Color.FromHex("#3c3f58");

    private readonly string _id;
    private readonly GifClientSystem _gifs;
    private readonly IGameTiming _timing;
    private readonly Label _placeholderLabel;

    private GifClientSystem.GifEntry? _entry;

    /// <param name="width">Frame width from the markup (0 if unknown), used to size the placeholder.</param>
    /// <param name="height">Frame height from the markup (0 if unknown), used to size the placeholder.</param>
    public GifControl(string id, string title, int width, int height, GifClientSystem gifs, IGameTiming timing)
    {
        _id = id;
        _gifs = gifs;
        _timing = timing;

        MouseFilter = MouseFilterMode.Stop;
        ToolTip = title;
        VerticalAlignment = VAlignment.Center;
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

        // Chat lines are laid out once when added, so size the placeholder to the final frame size when we know it.
        SetSize = width > 0 && height > 0
            ? DisplaySize(width, height)
            : new Vector2(PlaceholderWidth, PlaceholderHeight);

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

    private static Vector2 DisplaySize(int frameWidth, int frameHeight)
    {
        var scale = Math.Min(1f, MaxDisplayWidth / frameWidth);
        return new Vector2(MathF.Round(frameWidth * scale), MathF.Round(frameHeight * scale));
    }

    private void Apply(GifClientSystem.GifEntry entry)
    {
        _entry = entry;
        _placeholderLabel.Visible = false;

        var size = DisplaySize(entry.FrameWidth, entry.FrameHeight);
        if (size == SetSize)
            return;

        SetSize = size;
        InvalidateMeasure();

        // The chat OutputPanel measures inline controls only when a line is added or the panel is invalidated, so a
        // size change after the fact (markup without w/h) needs the panel re-laid out. Re-assigning the style box is
        // its only public way to do that.
        for (var parent = Parent; parent != null; parent = parent.Parent)
        {
            if (parent is not OutputPanel panel)
                continue;

            panel.StyleBoxOverride = panel.StyleBoxOverride;
            break;
        }
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

        var frame = GifClientSystem.FrameAt(_entry, _timing.RealTime);
        handle.DrawTextureRectRegion(_entry.Sheet, box, _entry.FrameRegions[frame]);
    }
}

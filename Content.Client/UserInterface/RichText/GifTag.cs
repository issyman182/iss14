// iss14: GIFs in chat via GifSnap
using System.Diagnostics.CodeAnalysis;
using Content.Client.Gifs;
using Content.Client.Gifs.UI;
using Content.Shared.Gifs;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.RichText;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Client.UserInterface.RichText;

/// <summary>
/// Renders an inline animated GIF: <c>[gif id="klipy_123" title="cat"]</c>. The id is a GifSnap id the server already
/// posted to chat; the sprite sheet is fetched from the server on demand by <see cref="GifClientSystem"/>.
/// </summary>
[UsedImplicitly]
public sealed partial class GifTag : IMarkupTagHandler
{
    [Dependency] private IEntityManager _entMan = default!;
    [Dependency] private IGameTiming _timing = default!;

    public string Name => "gif";

    public bool TryCreateControl(MarkupNode node, [NotNullWhen(true)] out Control? control)
    {
        control = null;

        if (!node.Attributes.TryGetValue("id", out var idParam) || !idParam.TryGetString(out var id))
            return false;

        if (!GifConstants.IsValidId(id))
            return false;

        var title = string.Empty;
        if (node.Attributes.TryGetValue("title", out var titleParam) && titleParam.TryGetString(out var t))
            title = t;

        if (title.Length > GifConstants.MaxTitleLength)
            title = title[..GifConstants.MaxTitleLength];

        // Optional frame size (pixels) so the line is laid out at its final size before the sheet arrives.
        var width = 0;
        var height = 0;
        if (node.Attributes.TryGetValue("w", out var wParam) && wParam.TryGetLong(out var w))
            width = (int) Math.Clamp(w.Value, 0, 4096);
        if (node.Attributes.TryGetValue("h", out var hParam) && hParam.TryGetLong(out var h))
            height = (int) Math.Clamp(h.Value, 0, 4096);

        if (!_entMan.TrySystem<GifClientSystem>(out var gifs))
            return false;

        control = new GifControl(id, title, width, height, gifs, _timing);
        return true;
    }
}

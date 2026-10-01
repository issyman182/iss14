// iss14: GIFs in chat via GifSnap
using System.Diagnostics.CodeAnalysis;
using Content.Client.Gifs;
using Content.Client.Gifs.UI;
using Content.Shared.Gifs;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.RichText;
using Robust.Shared.Configuration;
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
    [Dependency] private IConfigurationManager _cfg = default!;

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

        // Any w/h attributes (older markup) are accepted but ignored: the control is always the fixed box.
        if (!_entMan.TrySystem<GifClientSystem>(out var gifs))
            return false;

        control = new GifControl(id, title, gifs, _timing, _cfg);
        return true;
    }
}

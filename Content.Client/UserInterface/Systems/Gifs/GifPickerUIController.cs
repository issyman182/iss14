// iss14: GIFs in chat via GifSnap
using Content.Client.Gameplay;
using Content.Client.Gifs.UI;
using Content.Shared.CCVar;
using JetBrains.Annotations;
using Robust.Client.UserInterface.Controllers;
using Robust.Shared.Configuration;

namespace Content.Client.UserInterface.Systems.Gifs;

/// <summary>
/// Owns the single <see cref="GifPickerWindow"/> instance toggled by the chat box's GIF button.
/// </summary>
[UsedImplicitly]
public sealed partial class GifPickerUIController : UIController, IOnStateExited<GameplayState>
{
    [Dependency] private IConfigurationManager _cfg = default!;

    private GifPickerWindow? _window;

    public bool Enabled => _cfg.GetCVar(CCVars.GifsEnabled);

    public void Toggle()
    {
        if (!Enabled)
        {
            _window?.Close();
            return;
        }

        // Keep one instance around so search results (and their thumbnails) survive closing the window.
        _window ??= new GifPickerWindow();

        if (_window.IsOpen)
            _window.Close();
        else
            _window.OpenCentered();
    }

    public void OnStateExited(GameplayState state)
    {
        _window?.Close();
        _window?.Cleanup();
        _window = null;
    }
}

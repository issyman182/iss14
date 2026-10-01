// iss14: GIFs in chat via GifSnap
using Content.Client.Gameplay;
using Content.Client.Gifs;
using Content.Client.Gifs.UI;
using Content.Client.UserInterface.Systems.Chat;
using Content.Shared.CCVar;
using JetBrains.Annotations;
using Robust.Client.UserInterface.Controllers;
using Robust.Shared.Configuration;

namespace Content.Client.UserInterface.Systems.Gifs;

/// <summary>
/// Owns the single <see cref="GifPickerWindow"/> instance toggled by the chat box's GIF button, and echoes every
/// server-side GIF error into the chat panel so a failed send is never silent (the picker closes on click).
/// </summary>
[UsedImplicitly]
public sealed partial class GifPickerUIController : UIController, IOnStateExited<GameplayState>, IOnSystemChanged<GifClientSystem>
{
    [Dependency] private IConfigurationManager _cfg = default!;

    private GifPickerWindow? _window;

    public bool Enabled => _cfg.GetCVar(CCVars.GifsEnabled);

    public void OnSystemLoaded(GifClientSystem system)
    {
        system.Error += OnGifError;
    }

    public void OnSystemUnloaded(GifClientSystem system)
    {
        system.Error -= OnGifError;
    }

    private void OnGifError(string error)
    {
        var text = Loc.GetString("gifs-chat-error-prefix", ("error", error));
        UIManager.GetUIController<ChatUIController>().AddLocalNotice(text, Color.Orange);
    }

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

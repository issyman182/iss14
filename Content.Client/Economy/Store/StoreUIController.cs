// iss14: Economy
using Robust.Client.UserInterface.Controllers;

namespace Content.Client.Economy.Store;

/// <summary>Keeps a single store window so the lobby button, the character editor and loadout links share it.</summary>
public sealed class StoreUIController : UIController
{
    private StoreWindow? _window;

    public void Open()
    {
        if (_window is { IsOpen: true })
        {
            _window.MoveToFront();
            return;
        }

        _window = new StoreWindow();
        _window.OnClose += () => _window = null;
        _window.OpenCentered();
    }

    public void Toggle()
    {
        if (_window is { IsOpen: true })
        {
            _window.Close();
            return;
        }

        Open();
    }
}

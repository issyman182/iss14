// iss14: GIFs in chat via GifSnap
using Content.Client.Eui;
using Content.Shared.Administration;
using Content.Shared.Eui;
using JetBrains.Annotations;

namespace Content.Client.Administration.UI.GifConfig;

[UsedImplicitly]
public sealed class GifConfigEui : BaseEui
{
    private readonly GifConfigWindow _window;

    public GifConfigEui()
    {
        _window = new GifConfigWindow();

        _window.OnSet += (field, value) => SendMessage(new GifConfigSetMessage(field, value));
        _window.OnClose += () => SendMessage(new CloseEuiMessage());
    }

    public override void HandleState(EuiStateBase state)
    {
        if (state is GifConfigEuiState s)
            _window.SetState(s);
    }

    public override void Opened()
    {
        _window.OpenCentered();
    }

    public override void Closed()
    {
        _window.Close();
    }
}

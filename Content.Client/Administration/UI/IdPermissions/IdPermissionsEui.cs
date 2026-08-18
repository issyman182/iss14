using Content.Client.Eui;
using Content.Shared.Administration;
using Content.Shared.Eui;
using JetBrains.Annotations;

namespace Content.Client.Administration.UI.IdPermissions;

[UsedImplicitly]
public sealed class IdPermissionsEui : BaseEui
{
    private readonly IdPermissionsWindow _window;

    public IdPermissionsEui()
    {
        _window = new IdPermissionsWindow();

        _window.OnWrite += (fullName, jobTitle, jobProto, jobIcon, access) =>
            SendMessage(new IdPermissionsWriteMessage(fullName, jobTitle, jobProto, jobIcon, access));
        _window.OnClose += () => SendMessage(new CloseEuiMessage());
    }

    public override void HandleState(EuiStateBase state)
    {
        if (state is IdPermissionsEuiState s)
            _window.UpdateState(s);
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

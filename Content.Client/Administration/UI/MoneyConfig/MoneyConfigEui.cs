// iss14: Economy
using Content.Client.Eui;
using Content.Shared.Administration;
using Content.Shared.Eui;
using JetBrains.Annotations;

namespace Content.Client.Administration.UI.MoneyConfig;

[UsedImplicitly]
public sealed class MoneyConfigEui : BaseEui
{
    private readonly MoneyConfigWindow _window;

    public MoneyConfigEui()
    {
        _window = new MoneyConfigWindow();

        _window.OnSet += (field, value) => SendMessage(new MoneyConfigSetMessage(field, value));
        _window.OnSetSalary += (job, value) => SendMessage(new MoneyConfigSetSalaryMessage(job, value));
        _window.OnSetPrice += (proto, value) => SendMessage(new MoneyConfigSetPriceMessage(proto, value));
        _window.OnClose += () => SendMessage(new CloseEuiMessage());
    }

    public override void HandleState(EuiStateBase state)
    {
        if (state is MoneyConfigEuiState s)
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

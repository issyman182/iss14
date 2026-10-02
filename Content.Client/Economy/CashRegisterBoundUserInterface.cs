// iss14: Economy
using Content.Shared.Economy;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

namespace Content.Client.Economy;

[UsedImplicitly]
public sealed class CashRegisterBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    [ViewVariables]
    private CashRegisterWindow? _window;

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<CashRegisterWindow>();
        _window.OnCreateBill += items => SendMessage(new CashRegisterCreateBillMessage(items));
        _window.OnCancelBill += () => SendMessage(new CashRegisterCancelBillMessage());
        _window.OnSetPrice += (proto, price) => SendMessage(new CashRegisterSetPriceMessage(proto, price));
        _window.OnRefresh += () => SendMessage(new CashRegisterRefreshMessage());
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is CashRegisterBuiState s)
            _window?.SetState(s);
    }
}

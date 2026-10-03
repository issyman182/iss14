// iss14: Economy
using Content.Shared.Economy;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

namespace Content.Client.Economy;

/// <summary>Cashier side of the register (<see cref="CashRegisterUiKey.Key"/>).</summary>
[UsedImplicitly]
public sealed class CashRegisterBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    [ViewVariables]
    private CashRegisterWindow? _window;

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<CashRegisterWindow>();
        _window.OnAdjustDraft += (proto, delta) => SendMessage(new CashRegisterAdjustDraftMessage(proto, delta));
        _window.OnAddManual += (name, price, count) => SendMessage(new CashRegisterAddManualMessage(name, price, count));
        _window.OnRemoveLine += index => SendMessage(new CashRegisterRemoveLineMessage(index));
        _window.OnSetAccount += account => SendMessage(new CashRegisterSetAccountMessage(account));
        _window.OnClearDraft += () => SendMessage(new CashRegisterClearDraftMessage());
        _window.OnCreateBill += () => SendMessage(new CashRegisterCreateBillMessage());
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

/// <summary>Customer side of the register (<see cref="CashRegisterUiKey.Customer"/>): the bill and a pay button.</summary>
[UsedImplicitly]
public sealed class CashRegisterCustomerBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    [ViewVariables]
    private CashRegisterCustomerWindow? _window;

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<CashRegisterCustomerWindow>();
        _window.OnPay += () => SendMessage(new CashRegisterPayMessage());
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is CashRegisterCustomerBuiState s)
            _window?.SetState(s);
    }
}

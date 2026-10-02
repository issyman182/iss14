// iss14: Economy
using Content.Shared.Economy;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

namespace Content.Client.Economy;

[UsedImplicitly]
public sealed class AtmBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    [ViewVariables]
    private AtmWindow? _window;

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<AtmWindow>();
        _window.OnWithdraw += amount => SendMessage(new AtmWithdrawMessage(amount));
        _window.OnDeposit += () => SendMessage(new AtmDepositMessage());
        _window.OnVerify += () => SendMessage(new AtmVerifyHolderMessage());
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is AtmBoundUserInterfaceState s)
            _window?.SetState(s);
    }
}

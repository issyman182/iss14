// iss14: Economy
using Content.Shared.Economy;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

namespace Content.Client.Economy;

[UsedImplicitly]
public sealed class FineTerminalBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    [ViewVariables]
    private FineTerminalWindow? _window;

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<FineTerminalWindow>();
        _window.OnSelectRecord += record => SendMessage(new FineTerminalSelectRecordMessage(record));
        _window.OnIssue += (record, amount, reason) => SendMessage(new FineTerminalIssueMessage(record, amount, reason));
        _window.OnWaive += (record, fine) => SendMessage(new FineTerminalWaiveMessage(record, fine));
        _window.OnPayOwn += fine => SendMessage(new FineTerminalPayOwnMessage(fine));
        _window.OnPayClient += fine => SendMessage(new FineTerminalPayClientMessage(fine));
        _window.OnFileMandate += () => SendMessage(new FineTerminalFileMandateMessage());
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is FineTerminalBuiState s)
            _window?.SetState(s);
    }
}

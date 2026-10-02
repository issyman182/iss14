// iss14: Economy
using Content.Shared.Economy.Tasks;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

namespace Content.Client.Economy.Tasks;

[UsedImplicitly]
public sealed class TaskBoardBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    [ViewVariables]
    private TaskBoardWindow? _window;

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<TaskBoardWindow>();
        _window.OnClaim += id => SendMessage(new TaskBoardClaimMessage(id));
        _window.OnAbandon += id => SendMessage(new TaskBoardAbandonMessage(id));
        _window.OnConfirm += id => SendMessage(new TaskBoardConfirmMessage(id));
        _window.OnCancel += id => SendMessage(new TaskBoardCancelMessage(id));
        _window.OnCreate += (title, desc, reward) => SendMessage(new TaskBoardCreateMessage(title, desc, reward));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is TaskBoardBuiState s)
            _window?.SetState(s);
    }
}

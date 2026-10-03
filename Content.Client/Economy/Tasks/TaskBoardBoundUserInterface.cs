// iss14: Economy
using Content.Shared.Economy.Tasks;
using JetBrains.Annotations;
using Robust.Client.Player;
using Robust.Client.UserInterface;

namespace Content.Client.Economy.Tasks;

[UsedImplicitly]
public sealed partial class TaskBoardBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    [Dependency] private IPlayerManager _player = default!;

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
        _window.OnCreate += (department, title, desc, reward) => SendMessage(new TaskBoardCreateMessage(department, title, desc, reward));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is not TaskBoardBuiState s || _window == null)
            return;

        // The server lists every viewer's departments; pick ours.
        var departments = new List<TaskBoardDepartment>();
        if (_player.LocalEntity is { } local && s.Viewers.TryGetValue(EntMan.GetNetEntity(local), out var mine))
            departments = mine;

        _window.SetState(s, departments);
    }
}

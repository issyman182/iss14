// iss14: Economy
using Content.Server.EUI;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server.Administration.Commands;

[AdminCommand(AdminFlags.Admin)]
public sealed partial class MoneyConfigCommand : LocalizedCommands
{
    [Dependency] private EuiManager _euis = default!;

    public override string Command => "moneyconfig";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player is not { } admin)
        {
            shell.WriteError(Loc.GetString("cmd-moneyconfig-server"));
            return;
        }

        var ui = new MoneyConfigEui();
        _euis.OpenEui(ui, admin);
        ui.BuildState();
    }
}

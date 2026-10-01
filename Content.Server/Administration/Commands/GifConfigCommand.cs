// iss14: GIFs in chat via GifSnap
using Content.Server.EUI;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server.Administration.Commands;

[AdminCommand(AdminFlags.Server)]
public sealed partial class GifConfigCommand : LocalizedCommands
{
    [Dependency] private EuiManager _euis = default!;

    public override string Command => "gifconfig";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player is not { } admin)
        {
            shell.WriteError(Loc.GetString("cmd-gifconfig-server"));
            return;
        }

        var ui = new GifConfigEui();
        _euis.OpenEui(ui, admin);
        ui.BuildState();
    }
}

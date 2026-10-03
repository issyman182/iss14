// iss14
using Content.Server.EUI;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server.Administration.Commands;

/// <summary>Opens the entity finder: search entities by name, category and distance, see their positions, teleport.</summary>
[AdminCommand(AdminFlags.Admin)]
public sealed partial class EntityFinderCommand : LocalizedCommands
{
    [Dependency] private EuiManager _euis = default!;

    public override string Command => "entityfinder";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player is not { } admin)
        {
            shell.WriteError(Loc.GetString("cmd-entityfinder-server"));
            return;
        }

        var ui = new EntityFinderEui();
        _euis.OpenEui(ui, admin);
        ui.Search(string.Empty);
    }
}

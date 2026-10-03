using Content.Server.Administration;
using Content.Server.Power.EntitySystems;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._ISS14.Power.Commands;

/// <summary>
/// Toggles the "free power" debug mode: every machine/light/door that runs on APC power acts as powered,
/// so things can be tested without fixing the station's power first. Turns itself off on round restart.
/// </summary>
[AdminCommand(AdminFlags.Debug)]
public sealed partial class FreePowerCommand : LocalizedEntityCommands
{
    [Dependency] private PowerNetSystem _powerNet = default!;

    public override string Command => "freepower";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        bool enable;
        switch (args.Length)
        {
            case 0:
                enable = !_powerNet.FreePower;
                break;
            case 1 when args[0] is "on" or "true" or "1":
                enable = true;
                break;
            case 1 when args[0] is "off" or "false" or "0":
                enable = false;
                break;
            default:
                shell.WriteError(Loc.GetString("cmd-freepower-help"));
                return;
        }

        _powerNet.FreePower = enable;
        shell.WriteLine(Loc.GetString(enable ? "cmd-freepower-enabled" : "cmd-freepower-disabled"));
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length == 1
            ? CompletionResult.FromHintOptions(["on", "off"], Loc.GetString("cmd-freepower-arg"))
            : CompletionResult.Empty;
    }
}

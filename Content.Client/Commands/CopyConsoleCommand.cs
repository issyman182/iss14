using Content.Client.DebugConsole;
using Content.Shared.Administration;
using JetBrains.Annotations;
using Robust.Shared.Console;

namespace Content.Client.Commands;

/// <summary>
///     iss14: copies debug console output to the clipboard, since the engine console text can't be selected.
/// </summary>
[UsedImplicitly, AnyCommand]
public sealed partial class CopyConsoleCommand : LocalizedCommands
{
    [Dependency] private DebugConsoleCopyManager _copy = default!;

    public override string Command => "copycon";

    public override string Help => LocalizationManager.GetString($"cmd-{Command}-help", ("command", Command));

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var count = int.MaxValue;
        if (args.Length > 0 && (!int.TryParse(args[0], out count) || count <= 0))
        {
            shell.WriteError(Help);
            return;
        }

        var copied = _copy.CopyLines(count);
        shell.WriteLine(LocalizationManager.GetString($"cmd-{Command}-done", ("count", copied)));
    }
}

// iss14: Economy
using Content.Server.Administration;
using Content.Server.Economy.Store;
using Content.Shared.Administration;
using Content.Shared.Economy.Store;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.Prototypes;

namespace Content.Server.Economy.Commands;

/// <summary><c>storegrant &lt;player&gt; &lt;storeItem&gt;</c> — gives a store item for free.</summary>
[AdminCommand(AdminFlags.Admin)]
public sealed partial class StoreGrantCommand : LocalizedCommands
{
    [Dependency] private StoreManager _store = default!;
    [Dependency] private IPlayerLocator _locator = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IPrototypeManager _proto = default!;

    public override string Command => "storegrant";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2)
        {
            shell.WriteError(Loc.GetString("cmd-storegrant-invalid-arguments"));
            return;
        }

        if (!_proto.HasIndex<StoreItemPrototype>(args[1]))
        {
            shell.WriteError(Loc.GetString("cmd-storegrant-unknown-item"));
            return;
        }

        var located = await _locator.LookupIdByNameOrIdAsync(args[0]);
        if (located == null)
        {
            shell.WriteError(Loc.GetString("cmd-bank-invalid-player"));
            return;
        }

        shell.WriteLine(await _store.GrantAsync(located.UserId, args[1])
            ? Loc.GetString("cmd-storegrant-done")
            : Loc.GetString("cmd-storegrant-already"));
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 2)
            return CompletionResult.FromHintOptions(CompletionHelper.PrototypeIDs<StoreItemPrototype>(), Loc.GetString("cmd-store-completion-item"));

        return BankCommandHelpers.CompletePlayer(_players, args, Loc.GetString("cmd-bank-completion-player"));
    }
}

/// <summary><c>storerevoke &lt;player&gt; &lt;storeItem&gt;</c> — takes a store item away.</summary>
[AdminCommand(AdminFlags.Admin)]
public sealed partial class StoreRevokeCommand : LocalizedCommands
{
    [Dependency] private StoreManager _store = default!;
    [Dependency] private IPlayerLocator _locator = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IPrototypeManager _proto = default!;

    public override string Command => "storerevoke";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2)
        {
            shell.WriteError(Loc.GetString("cmd-storerevoke-invalid-arguments"));
            return;
        }

        var located = await _locator.LookupIdByNameOrIdAsync(args[0]);
        if (located == null)
        {
            shell.WriteError(Loc.GetString("cmd-bank-invalid-player"));
            return;
        }

        shell.WriteLine(await _store.RevokeAsync(located.UserId, args[1])
            ? Loc.GetString("cmd-storerevoke-done")
            : Loc.GetString("cmd-storerevoke-not-owned"));
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 2)
            return CompletionResult.FromHintOptions(CompletionHelper.PrototypeIDs<StoreItemPrototype>(), Loc.GetString("cmd-store-completion-item"));

        return BankCommandHelpers.CompletePlayer(_players, args, Loc.GetString("cmd-bank-completion-player"));
    }
}

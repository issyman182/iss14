// iss14: Economy
using System.Globalization;
using System.Linq;
using Content.Server.Administration;
using Content.Server.Database;
using Content.Shared.Administration;
using Robust.Server.Player;
using Robust.Shared.Console;

namespace Content.Server.Economy.Commands;

/// <summary><c>bank &lt;player&gt;</c> — shows a player's bank balance (online or offline).</summary>
[AdminCommand(AdminFlags.Admin)]
public sealed partial class BankCommand : LocalizedCommands
{
    [Dependency] private IBankManager _bank = default!;
    [Dependency] private IPlayerLocator _locator = default!;
    [Dependency] private IPlayerManager _players = default!;

    public override string Command => "bank";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Loc.GetString("cmd-bank-invalid-arguments"));
            return;
        }

        var located = await _locator.LookupIdByNameOrIdAsync(args[0]);
        if (located == null)
        {
            shell.WriteError(Loc.GetString("cmd-bank-invalid-player"));
            return;
        }

        var balance = await _bank.GetBalanceAsync(located.UserId);
        shell.WriteLine(Loc.GetString("cmd-bank-result", ("player", located.Username), ("balance", balance)));
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
        => BankCommandHelpers.CompletePlayer(_players, args, Loc.GetString("cmd-bank-completion-player"));
}

/// <summary><c>bankset &lt;player&gt; &lt;amount&gt;</c> — overwrites a player's balance.</summary>
[AdminCommand(AdminFlags.Admin)]
public sealed partial class BankSetCommand : LocalizedCommands
{
    [Dependency] private IBankManager _bank = default!;
    [Dependency] private IPlayerLocator _locator = default!;
    [Dependency] private IPlayerManager _players = default!;

    public override string Command => "bankset";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2 || !long.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount))
        {
            shell.WriteError(Loc.GetString("cmd-bankset-invalid-arguments"));
            return;
        }

        var located = await _locator.LookupIdByNameOrIdAsync(args[0]);
        if (located == null)
        {
            shell.WriteError(Loc.GetString("cmd-bank-invalid-player"));
            return;
        }

        var admin = shell.Player?.Name ?? "server";
        await _bank.SetBalanceAsync(located.UserId, amount, $"admin bankset by {admin}");
        var balance = await _bank.GetBalanceAsync(located.UserId);
        shell.WriteLine(Loc.GetString("cmd-bank-result", ("player", located.Username), ("balance", balance)));
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 2)
            return CompletionResult.FromHint(Loc.GetString("cmd-bankset-completion-amount"));

        return BankCommandHelpers.CompletePlayer(_players, args, Loc.GetString("cmd-bank-completion-player"));
    }
}

/// <summary><c>bankadd &lt;player&gt; &lt;delta&gt; [reason]</c> — adds (or with a negative delta removes) credits.</summary>
[AdminCommand(AdminFlags.Admin)]
public sealed partial class BankAddCommand : LocalizedCommands
{
    [Dependency] private IBankManager _bank = default!;
    [Dependency] private IPlayerLocator _locator = default!;
    [Dependency] private IPlayerManager _players = default!;

    public override string Command => "bankadd";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 2 || !long.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var delta))
        {
            shell.WriteError(Loc.GetString("cmd-bankadd-invalid-arguments"));
            return;
        }

        var located = await _locator.LookupIdByNameOrIdAsync(args[0]);
        if (located == null)
        {
            shell.WriteError(Loc.GetString("cmd-bank-invalid-player"));
            return;
        }

        var admin = shell.Player?.Name ?? "server";
        var reason = args.Length > 2 ? string.Join(' ', args.Skip(2)) : "no reason given";
        var result = await _bank.AdjustBalanceAsync(located.UserId, delta, $"admin bankadd by {admin}: {reason}");

        if (result == null)
        {
            shell.WriteError(Loc.GetString("cmd-bankadd-negative"));
            return;
        }

        shell.WriteLine(Loc.GetString("cmd-bank-result", ("player", located.Username), ("balance", result.Value)));
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length switch
        {
            2 => CompletionResult.FromHint(Loc.GetString("cmd-bankadd-completion-delta")),
            3 => CompletionResult.FromHint(Loc.GetString("cmd-bankadd-completion-reason")),
            _ => BankCommandHelpers.CompletePlayer(_players, args, Loc.GetString("cmd-bank-completion-player")),
        };
    }
}

/// <summary><c>banktop [n]</c> — the richest accounts.</summary>
[AdminCommand(AdminFlags.Admin)]
public sealed partial class BankTopCommand : LocalizedCommands
{
    [Dependency] private IServerDbManager _db = default!;

    public override string Command => "banktop";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var count = 10;
        if (args.Length > 0 && (!int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out count) || count <= 0))
        {
            shell.WriteError(Loc.GetString("cmd-banktop-invalid-arguments"));
            return;
        }

        var rows = await _db.GetTopBankAccountsAsync(Math.Min(count, 100));
        if (rows.Count == 0)
        {
            shell.WriteLine(Loc.GetString("cmd-banktop-empty"));
            return;
        }

        var i = 1;
        foreach (var row in rows)
        {
            shell.WriteLine(Loc.GetString("cmd-banktop-row",
                ("rank", i++),
                ("player", row.UserName ?? row.UserId.ToString()),
                ("balance", row.Balance),
                ("earned", row.LifetimeEarned)));
        }
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
        => args.Length == 1 ? CompletionResult.FromHint(Loc.GetString("cmd-banktop-completion-count")) : CompletionResult.Empty;
}

/// <summary><c>balance</c> — any player: prints their own balance.</summary>
[AnyCommand]
public sealed partial class BalanceCommand : LocalizedCommands
{
    [Dependency] private IBankManager _bank = default!;

    public override string Command => "balance";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player is not { } player)
        {
            shell.WriteError(Loc.GetString("cmd-balance-server"));
            return;
        }

        shell.WriteLine(Loc.GetString("cmd-balance-result", ("balance", _bank.GetBalance(player.UserId))));
    }
}

internal static class BankCommandHelpers
{
    public static CompletionResult CompletePlayer(IPlayerManager players, string[] args, string hint)
    {
        if (args.Length != 1)
            return CompletionResult.Empty;

        var options = players.Sessions.OrderBy(c => c.Name).Select(c => c.Name).ToArray();
        return CompletionResult.FromHintOptions(options, hint);
    }
}

/// <summary><c>banklog &lt;player|account&gt; [count]</c> — newest transactions involving a player or a department account.</summary>
[AdminCommand(AdminFlags.Admin)]
public sealed partial class BankLogCommand : LocalizedCommands
{
    [Dependency] private IServerDbManager _db = default!;
    [Dependency] private IPlayerLocator _locator = default!;
    [Dependency] private IPlayerManager _players = default!;

    public override string Command => "banklog";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length is < 1 or > 2)
        {
            shell.WriteError(Loc.GetString("cmd-banklog-invalid-arguments"));
            return;
        }

        var count = 20;
        if (args.Length == 2 && (!int.TryParse(args[1], out count) || count <= 0))
        {
            shell.WriteError(Loc.GetString("cmd-banklog-invalid-arguments"));
            return;
        }

        // A player name/guid, else taken literally (department account ids like "Service").
        var party = args[0];
        var located = await _locator.LookupIdByNameOrIdAsync(args[0]);
        if (located != null)
            party = located.UserId.ToString();

        var rows = await _db.GetBankTransactionsAsync(party, Math.Min(count, 200));
        if (rows.Count == 0)
        {
            shell.WriteLine(Loc.GetString("cmd-banklog-empty"));
            return;
        }

        foreach (var row in rows)
        {
            shell.WriteLine(Loc.GetString("cmd-banklog-row",
                ("time", row.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
                ("round", row.RoundId?.ToString() ?? "-"),
                ("from", $"{row.FromKind}:{row.FromId}"),
                ("to", $"{row.ToKind}:{row.ToId}"),
                ("amount", row.Amount),
                ("reason", row.Reason)));
        }
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 2)
            return CompletionResult.FromHint(Loc.GetString("cmd-banklog-completion-count"));

        return BankCommandHelpers.CompletePlayer(_players, args, Loc.GetString("cmd-banklog-completion-party"));
    }
}

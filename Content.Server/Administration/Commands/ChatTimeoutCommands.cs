// iss14: GIFs in chat via GifSnap (chat timeouts / GIF mutes)
using System.Linq;
using Content.Server.Administration.Logs;
using Content.Server.Administration.Managers;
using Content.Server.Chat.Managers;
using Content.Shared.Administration;
using Content.Shared.Database;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.Network;

namespace Content.Server.Administration.Commands;

/// <summary>Shared implementation of the <c>timeout</c>/<c>gifmute</c> command families.</summary>
public abstract partial class BaseChatTimeoutCommand : LocalizedCommands
{
    [Dependency] private ChatTimeoutManager _timeouts = default!;
    [Dependency] private IPlayerLocator _locator = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IAdminLogManager _adminLog = default!;
    [Dependency] private IChatManager _chat = default!;

    protected abstract ChatTimeoutManager.Kind Kind { get; }

    /// <summary>Locale key prefix: <c>cmd-timeout</c> or <c>cmd-gifmute</c>.</summary>
    protected abstract string LocPrefix { get; }

    protected CompletionResult PlayerCompletion(string[] args)
    {
        if (args.Length != 1)
            return CompletionResult.Empty;

        var options = _players.Sessions.Select(c => c.Name).OrderBy(c => c).ToArray();
        return CompletionResult.FromHintOptions(options, Loc.GetString($"{LocPrefix}-hint-player"));
    }

    protected async void ExecuteSet(IConsoleShell shell, string[] args)
    {
        if (args.Length < 2)
        {
            shell.WriteLine(Help);
            return;
        }

        if (!int.TryParse(args[1], out var minutes) || minutes <= 0 || minutes > 60 * 24 * 365)
        {
            shell.WriteError(Loc.GetString($"{LocPrefix}-invalid-minutes", ("minutes", args[1])));
            return;
        }

        var reason = args.Length > 2 ? string.Join(' ', args.Skip(2)) : Loc.GetString($"{LocPrefix}-default-reason");

        var located = await _locator.LookupIdByNameOrIdAsync(args[0]);
        if (located == null)
        {
            shell.WriteError(Loc.GetString($"{LocPrefix}-player-not-found", ("player", args[0])));
            return;
        }

        var entry = _timeouts.Set(Kind, located.UserId, located.Username, TimeSpan.FromMinutes(minutes), reason, shell.Player?.UserId);
        shell.WriteLine(Loc.GetString($"{LocPrefix}-set", ("player", located.Username), ("minutes", minutes), ("reason", reason)));

        _adminLog.Add(LogType.Chat, LogImpact.Medium,
            $"{(shell.Player != null ? $"{shell.Player:Player}" : "Server")} set a {Kind} timeout on {located.Username} ({located.UserId}) for {minutes} minutes: {reason}");

        // Tell the target if they are online.
        if (_players.TryGetSessionById(located.UserId, out var session))
        {
            _chat.DispatchServerMessage(session, Loc.GetString($"{LocPrefix}-notice-target",
                ("minutes", ChatTimeoutManager.RemainingMinutes(entry)), ("reason", reason)), suppressLog: true);
        }
    }

    protected async void ExecuteRemove(IConsoleShell shell, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteLine(Help);
            return;
        }

        var located = await _locator.LookupIdByNameOrIdAsync(args[0]);
        if (located == null)
        {
            shell.WriteError(Loc.GetString($"{LocPrefix}-player-not-found", ("player", args[0])));
            return;
        }

        if (!_timeouts.Remove(Kind, located.UserId))
        {
            shell.WriteLine(Loc.GetString($"{LocPrefix}-none", ("player", located.Username)));
            return;
        }

        shell.WriteLine(Loc.GetString($"{LocPrefix}-removed", ("player", located.Username)));
        _adminLog.Add(LogType.Chat, LogImpact.Medium,
            $"{(shell.Player != null ? $"{shell.Player:Player}" : "Server")} lifted the {Kind} timeout on {located.Username} ({located.UserId})");

        if (_players.TryGetSessionById(located.UserId, out var session))
            _chat.DispatchServerMessage(session, Loc.GetString($"{LocPrefix}-notice-lifted"), suppressLog: true);
    }

    protected void ExecuteList(IConsoleShell shell)
    {
        var active = _timeouts.Active(Kind);
        if (active.Count == 0)
        {
            shell.WriteLine(Loc.GetString($"{LocPrefix}-list-empty"));
            return;
        }

        foreach (var entry in active)
        {
            shell.WriteLine(Loc.GetString($"{LocPrefix}-list-entry",
                ("player", entry.Name),
                ("minutes", ChatTimeoutManager.RemainingMinutes(entry)),
                ("reason", entry.Reason)));
        }
    }
}

[AdminCommand(AdminFlags.Moderator)]
public sealed partial class TimeoutCommand : BaseChatTimeoutCommand
{
    public override string Command => "timeout";
    protected override ChatTimeoutManager.Kind Kind => ChatTimeoutManager.Kind.Chat;
    protected override string LocPrefix => "cmd-timeout";

    public override void Execute(IConsoleShell shell, string argStr, string[] args) => ExecuteSet(shell, args);
    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args) => PlayerCompletion(args);
}

[AdminCommand(AdminFlags.Moderator)]
public sealed partial class UntimeoutCommand : BaseChatTimeoutCommand
{
    public override string Command => "untimeout";
    protected override ChatTimeoutManager.Kind Kind => ChatTimeoutManager.Kind.Chat;
    protected override string LocPrefix => "cmd-timeout";

    public override void Execute(IConsoleShell shell, string argStr, string[] args) => ExecuteRemove(shell, args);
    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args) => PlayerCompletion(args);
}

[AdminCommand(AdminFlags.Moderator)]
public sealed partial class TimeoutsCommand : BaseChatTimeoutCommand
{
    public override string Command => "timeouts";
    protected override ChatTimeoutManager.Kind Kind => ChatTimeoutManager.Kind.Chat;
    protected override string LocPrefix => "cmd-timeout";

    public override void Execute(IConsoleShell shell, string argStr, string[] args) => ExecuteList(shell);
}

[AdminCommand(AdminFlags.Moderator)]
public sealed partial class GifMuteCommand : BaseChatTimeoutCommand
{
    public override string Command => "gifmute";
    protected override ChatTimeoutManager.Kind Kind => ChatTimeoutManager.Kind.Gif;
    protected override string LocPrefix => "cmd-gifmute";

    public override void Execute(IConsoleShell shell, string argStr, string[] args) => ExecuteSet(shell, args);
    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args) => PlayerCompletion(args);
}

[AdminCommand(AdminFlags.Moderator)]
public sealed partial class GifUnmuteCommand : BaseChatTimeoutCommand
{
    public override string Command => "gifunmute";
    protected override ChatTimeoutManager.Kind Kind => ChatTimeoutManager.Kind.Gif;
    protected override string LocPrefix => "cmd-gifmute";

    public override void Execute(IConsoleShell shell, string argStr, string[] args) => ExecuteRemove(shell, args);
    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args) => PlayerCompletion(args);
}

[AdminCommand(AdminFlags.Moderator)]
public sealed partial class GifMutesCommand : BaseChatTimeoutCommand
{
    public override string Command => "gifmutes";
    protected override ChatTimeoutManager.Kind Kind => ChatTimeoutManager.Kind.Gif;
    protected override string LocPrefix => "cmd-gifmute";

    public override void Execute(IConsoleShell shell, string argStr, string[] args) => ExecuteList(shell);
}

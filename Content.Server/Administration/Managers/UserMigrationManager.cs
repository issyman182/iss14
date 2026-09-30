using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Administration;
using Content.Server.Chat.Managers;
using Content.Server.Database;
using Content.Shared.Database;
using Robust.Server.Player;
using Robust.Shared.IoC;
using Robust.Shared.Localization;
using Robust.Shared.Log;
using Robust.Shared.Network;

namespace Content.Server.Administration.Managers;

/// <summary>
/// Owns all user-data migration logic: re-pointing one GUID's data onto another. Three entry points feed
/// into the same DB operation (<see cref="IServerDbManager.MigrateUserDataAsync"/>):
/// <list type="bullet">
/// <item>the automatic same-username path called from <c>ConnectionManager</c>;</item>
/// <item>manual admin transfers from the <c>migrations</c> EUI;</item>
/// <item>admin approval of a pending (name-only) migration.</item>
/// </list>
/// See the <c>[[dual-hub-auth]]</c> context: the auth servers hand out a fresh GUID when a player
/// re-registers a username, orphaning their old GUID-keyed data. This re-attaches it.
/// </summary>
public sealed partial class UserMigrationManager : IPostInjectInit
{
    [Dependency] private IServerDbManager _db = default!;
    [Dependency] private IChatManager _chat = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private ILogManager _logManager = default!;

    private ISawmill _sawmill = default!;

    /// <summary>Result of a migration attempt for surfacing back to an admin in the EUI.</summary>
    public readonly record struct MigrationOutcome(bool Success, string Message);

    /// <summary>
    /// Called for every connecting user. If an older account with the same username exists and the new
    /// connection demonstrably belongs to the same person (shared HWID), their data is migrated
    /// automatically. A name-only or IP-only match is instead recorded as <see cref="MigrationStatus.Pending"/> for an
    /// admin to review — never auto-applied, to avoid account takeover via username re-registration.
    /// </summary>
    public async Task TryAutoMigrateAsync(
        NetUserId newUserId,
        string userName,
        ImmutableTypedHwid? newHwid,
        IPAddress? address,
        CancellationToken cancel = default)
    {
        // Loopback connections (a dev box, or the host playing on their own server) all share one
        // machine HWID and the loopback IP, so the same-person heuristics below would match unrelated
        // same-username accounts (e.g. an auth account vs. a local guest) and destructively re-point
        // their prefs. Never auto-migrate for localhost; admins can still migrate manually if needed.
        if (address != null && IsLoopback(address))
        {
            _sawmill.Debug($"Skipping auto-migration for loopback connection {userName} ({newUserId}).");
            return;
        }

        try
        {
            var candidates = await _db.GetPlayerRecordsByUserNameAsync(userName, cancel);

            PlayerRecord? matched = null;
            var matchReason = string.Empty;
            PlayerRecord? nameOnly = null;
            var pendingReason = "name-only"; // iss14 fix: "ip" when the pending candidate also shares the IP

            foreach (var candidate in candidates)
            {
                if (candidate.UserId == newUserId)
                    continue;

                // Skip accounts whose data already moved away, so we don't reprocess them.
                if (await _db.IsCompletedMigrationSourceAsync(candidate.UserId.UserId, cancel))
                    continue;

                if (HwidOverlap(candidate.HWId, newHwid))
                {
                    matched = candidate;
                    matchReason = "hwid";
                    break;
                }

                // iss14 fix: a bare IP match is not proof of identity (shared NAT, VPNs, dynamic ranges), so it
                // is no longer auto-applied. It is recorded as a pending migration for admin review instead,
                // preferring an IP match over a name-only match as the pending candidate.
                if (address != null && candidate.LastSeenAddress != null && candidate.LastSeenAddress.Equals(address))
                {
                    if (pendingReason != "ip")
                    {
                        nameOnly = candidate;
                        pendingReason = "ip";
                    }
                    continue;
                }

                // Records come back newest-first, so the first one is the best name-only fallback.
                nameOnly ??= candidate;
            }

            if (matched != null)
            {
                // Merge so nothing the new account already accrued is lost (it's usually empty anyway).
                var detail = await _db.MigrateUserDataAsync(
                    matched.UserId.UserId, newUserId.UserId, MigrationScope.Auto, merge: true, cancel);

                await _db.AddMigrationLogAsync(NewLog(
                    matched, newUserId, userName, automatic: true,
                    MigrationStatus.Completed, MigrationScope.Auto, matchReason, performedBy: null, detail));

                _sawmill.Info(
                    $"Auto-migrated data from {matched.UserId} to {newUserId} ({matchReason}): {detail}");
                _chat.SendAdminAlert(Loc.GetString("migration-alert-auto",
                    ("user", userName), ("reason", matchReason), ("detail", detail)));
                return;
            }

            if (nameOnly != null
                && !await _db.MigrationExistsAsync(nameOnly.UserId.UserId, newUserId.UserId, cancel))
            {
                await _db.AddMigrationLogAsync(NewLog(
                    nameOnly, newUserId, userName, automatic: true,
                    MigrationStatus.Pending, MigrationScope.Auto, pendingReason, performedBy: null, detail: null));

                _sawmill.Info(
                    $"Recorded pending migration {nameOnly.UserId} -> {newUserId} ({pendingReason} match for {userName})");
                _chat.SendAdminAlert(Loc.GetString("migration-alert-pending", ("user", userName)));
            }
        }
        catch (Exception e)
        {
            // Never let a migration problem block a login.
            _sawmill.Error($"Error during auto-migration for {userName} ({newUserId}): {e}");
        }
    }

    /// <summary>
    /// Manually move data from <paramref name="source"/> to <paramref name="target"/>. Both must be offline,
    /// since an online session holds authoritative in-memory play-time/prefs that would clobber the write.
    /// </summary>
    public async Task<MigrationOutcome> PerformManualAsync(
        LocatedPlayerData source,
        LocatedPlayerData target,
        MigrationScope scope,
        bool merge,
        NetUserId admin)
    {
        if (source.UserId == target.UserId)
            return new MigrationOutcome(false, Loc.GetString("migration-error-same-user"));

        if (_players.TryGetSessionById(target.UserId, out _))
            return new MigrationOutcome(false, Loc.GetString("migration-error-target-online"));

        if (_players.TryGetSessionById(source.UserId, out _))
            return new MigrationOutcome(false, Loc.GetString("migration-error-source-online"));

        var detail = await _db.MigrateUserDataAsync(source.UserId.UserId, target.UserId.UserId, scope, merge);

        await _db.AddMigrationLogAsync(new MigrationLog
        {
            SourceUserId = source.UserId.UserId,
            TargetUserId = target.UserId.UserId,
            SourceUserName = source.Username,
            TargetUserName = target.Username,
            Time = DateTime.UtcNow,
            Automatic = false,
            Status = MigrationStatus.Completed,
            Scope = scope,
            MatchReason = "manual",
            PerformedByUserId = admin.UserId,
            Detail = detail,
        });

        _sawmill.Info($"{admin} manually migrated {source.UserId} -> {target.UserId} [{scope}]: {detail}");
        return new MigrationOutcome(true, detail);
    }

    /// <summary>Approve a pending (name-only) migration, performing the move with its recorded scope.</summary>
    public async Task<MigrationOutcome> ApprovePendingAsync(int logId, NetUserId admin)
    {
        var log = await _db.GetMigrationLogAsync(logId);
        if (log is not { Status: MigrationStatus.Pending })
            return new MigrationOutcome(false, Loc.GetString("migration-error-not-pending"));

        if (_players.TryGetSessionById(new NetUserId(log.TargetUserId), out _))
            return new MigrationOutcome(false, Loc.GetString("migration-error-target-online"));

        // iss14 fix: same as PerformManualAsync, an online source session would clobber the migrated data.
        if (_players.TryGetSessionById(new NetUserId(log.SourceUserId), out _))
            return new MigrationOutcome(false, Loc.GetString("migration-error-source-online"));

        // Merge when approving so any data on the (possibly active) new account isn't discarded.
        var detail = await _db.MigrateUserDataAsync(log.SourceUserId, log.TargetUserId, log.Scope, merge: true);
        await _db.UpdateMigrationLogStatusAsync(logId, MigrationStatus.Completed, admin.UserId, detail);

        _sawmill.Info($"{admin} approved pending migration #{logId} ({log.SourceUserId} -> {log.TargetUserId}): {detail}");
        return new MigrationOutcome(true, detail);
    }

    /// <summary>Reject a pending migration, leaving all data untouched.</summary>
    public async Task<MigrationOutcome> RejectPendingAsync(int logId, NetUserId admin)
    {
        var log = await _db.GetMigrationLogAsync(logId);
        if (log is not { Status: MigrationStatus.Pending })
            return new MigrationOutcome(false, Loc.GetString("migration-error-not-pending"));

        await _db.UpdateMigrationLogStatusAsync(logId, MigrationStatus.Rejected, admin.UserId, null);
        return new MigrationOutcome(true, Loc.GetString("migration-rejected"));
    }

    private static MigrationLog NewLog(
        PlayerRecord source,
        NetUserId target,
        string targetName,
        bool automatic,
        MigrationStatus status,
        MigrationScope scope,
        string matchReason,
        Guid? performedBy,
        string? detail)
    {
        return new MigrationLog
        {
            SourceUserId = source.UserId.UserId,
            TargetUserId = target.UserId,
            SourceUserName = source.LastSeenUserName,
            TargetUserName = targetName,
            Time = DateTime.UtcNow,
            Automatic = automatic,
            Status = status,
            Scope = scope,
            MatchReason = matchReason,
            PerformedByUserId = performedBy,
            Detail = detail,
        };
    }

    /// <summary>
    /// True for 127.0.0.0/8, ::1, and the IPv4-mapped loopback (::ffff:127.0.0.1) the engine often hands us.
    /// </summary>
    private static bool IsLoopback(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        return IPAddress.IsLoopback(address);
    }

    private static bool HwidOverlap(ImmutableTypedHwid? a, ImmutableTypedHwid? b)
    {
        if (a == null || b == null)
            return false;

        return a.Type == b.Type && a.Hwid.AsSpan().SequenceEqual(b.Hwid.AsSpan());
    }

    void IPostInjectInit.PostInject()
    {
        _sawmill = _logManager.GetSawmill("migration");
    }
}

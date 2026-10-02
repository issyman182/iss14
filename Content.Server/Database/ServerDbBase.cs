using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Administration.Logs;
using Content.Shared.Administration.Logs;
using Content.Shared.Construction.Prototypes;
using Content.Shared.Database;
using Content.Shared.Humanoid;
using Content.Shared.Players.PlayTimeTracking;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Utility;

namespace Content.Server.Database
{
    public abstract class ServerDbBase
    {
        private readonly ISawmill _opsLog;
        public event Action<DatabaseNotification>? OnNotificationReceived;
        private readonly ISerializationManager _serialization;

        /// <param name="opsLog">Sawmill to trace log database operations to.</param>
        public ServerDbBase(ISawmill opsLog, ISerializationManager serialization)
        {
            _serialization = serialization;
            _opsLog = opsLog;
        }

        #region Preferences
        public async Task<Preference?> GetPlayerPreferencesAsync(
            NetUserId userId,
            CancellationToken cancel = default)
        {
            await using var db = await GetDb(cancel);

            return await db.DbContext
                .Preference
                .Include(p => p.Profiles).ThenInclude(h => h.Jobs)
                .Include(p => p.Profiles).ThenInclude(h => h.Antags)
                .Include(p => p.Profiles).ThenInclude(h => h.Traits)
                .Include(p => p.Profiles)
                    .ThenInclude(h => h.Loadouts)
                    .ThenInclude(l => l.Groups)
                    .ThenInclude(group => group.Loadouts)
                .AsSplitQuery()
                .SingleOrDefaultAsync(p => p.UserId == userId.UserId, cancel);
        }

        public async Task SaveSelectedCharacterIndexAsync(NetUserId userId, int index)
        {
            await using var db = await GetDb();

            await SetSelectedCharacterSlotAsync(userId, index, db.DbContext);

            await db.DbContext.SaveChangesAsync();
        }

        /// <summary>
        /// Only intended for use in unit tests - drops the organ marking data from a profile in the given slot
        /// </summary>
        /// <param name="userId">The user whose profile to modify</param>
        /// <param name="slot">The slot index to modify</param>
        public async Task MakeCharacterSlotLegacyAsync(NetUserId userId, int slot)
        {
            await using var db = await GetDb();

            var oldProfile = await db.DbContext.Profile
                .Include(p => p.Preference)
                .Where(p => p.Preference.UserId == userId.UserId)
                .AsSplitQuery()
                .SingleOrDefaultAsync(h => h.Slot == slot);

            if (oldProfile == null)
                return;

            oldProfile.OrganMarkings = null;
            oldProfile.Markings = JsonSerializer.SerializeToDocument(new List<string>());

            await db.DbContext.SaveChangesAsync();
        }

        public async Task SaveCharacterSlotAsync(NetUserId userId, HumanoidCharacterProfile? humanoid, int slot)
        {
            await using var db = await GetDb();

            if (humanoid is null)
            {
                await DeleteCharacterSlot(db.DbContext, userId, slot);
                await db.DbContext.SaveChangesAsync();
                return;
            }

            var oldProfile = db.DbContext.Profile
                .Include(p => p.Preference)
                .Where(p => p.Preference.UserId == userId.UserId)
                .Include(p => p.Jobs)
                .Include(p => p.Antags)
                .Include(p => p.Traits)
                .Include(p => p.Loadouts)
                    .ThenInclude(l => l.Groups)
                    .ThenInclude(group => group.Loadouts)
                .AsSplitQuery()
                .SingleOrDefault(h => h.Slot == slot);

            var newProfile = ConvertProfiles(humanoid, slot, oldProfile);
            if (oldProfile == null)
            {
                var prefs = await db.DbContext
                    .Preference
                    .Include(p => p.Profiles)
                    .SingleAsync(p => p.UserId == userId.UserId);

                prefs.Profiles.Add(newProfile);
            }

            await db.DbContext.SaveChangesAsync();
        }

        private static async Task DeleteCharacterSlot(ServerDbContext db, NetUserId userId, int slot)
        {
            var profile = await db.Profile.Include(p => p.Preference)
                .Where(p => p.Preference.UserId == userId.UserId && p.Slot == slot)
                .SingleOrDefaultAsync();

            if (profile == null)
            {
                return;
            }

            db.Profile.Remove(profile);
        }

        public async Task<Preference> InitPrefsAsync(NetUserId userId, HumanoidCharacterProfile defaultProfile)
        {
            await using var db = await GetDb();

            var profile = ConvertProfiles((HumanoidCharacterProfile) defaultProfile, 0);
            var prefs = new Preference
            {
                UserId = userId.UserId,
                SelectedCharacterSlot = 0,
                AdminOOCColor = Color.Red.ToHex(),
                ConstructionFavorites = [],
            };

            prefs.Profiles.Add(profile);

            db.DbContext.Preference.Add(prefs);

            await db.DbContext.SaveChangesAsync();

            return prefs;
        }

        public async Task DeleteSlotAndSetSelectedIndex(NetUserId userId, int deleteSlot, int newSlot)
        {
            await using var db = await GetDb();

            await DeleteCharacterSlot(db.DbContext, userId, deleteSlot);
            await SetSelectedCharacterSlotAsync(userId, newSlot, db.DbContext);

            await db.DbContext.SaveChangesAsync();
        }

        public async Task SaveAdminOOCColorAsync(NetUserId userId, Color color)
        {
            await using var db = await GetDb();
            var prefs = await db.DbContext
                .Preference
                .Include(p => p.Profiles)
                .SingleAsync(p => p.UserId == userId.UserId);
            prefs.AdminOOCColor = color.ToHex();

            await db.DbContext.SaveChangesAsync();

        }

        public async Task SaveConstructionFavoritesAsync(NetUserId userId, List<ProtoId<ConstructionPrototype>> constructionFavorites)
        {
            await using var db = await GetDb();
            var prefs = await db.DbContext.Preference.SingleAsync(p => p.UserId == userId.UserId);

            var favorites = new List<string>(constructionFavorites.Count);
            foreach (var favorite in constructionFavorites)
                favorites.Add(favorite.Id);
            prefs.ConstructionFavorites = favorites;

            await db.DbContext.SaveChangesAsync();
        }

        private static async Task SetSelectedCharacterSlotAsync(NetUserId userId, int newSlot, ServerDbContext db)
        {
            var prefs = await db.Preference.SingleAsync(p => p.UserId == userId.UserId);
            prefs.SelectedCharacterSlot = newSlot;
        }

        private Profile ConvertProfiles(HumanoidCharacterProfile humanoid, int slot, Profile? profile = null)
        {
            profile ??= new Profile();
            var appearance = humanoid.Appearance;

            // Shitmed Change: part-based body rollback - markings are a flat list again, stored in the legacy column
            List<string> markingStrings = new();
            foreach (var marking in appearance.Markings)
            {
                markingStrings.Add(marking.ToString());
            }

            profile.CharacterName = humanoid.Name;
            profile.FlavorText = humanoid.FlavorText;
            profile.Species = humanoid.Species;
            profile.Age = humanoid.Age;
            profile.Sex = humanoid.Sex.ToString();
            profile.Voice = humanoid.Voice.ToString();
            profile.Gender = humanoid.Gender.ToString();
            profile.EyeColor = appearance.EyeColor.ToHex();
            profile.SkinColor = appearance.SkinColor.ToHex();
            profile.SpawnPriority = (int) humanoid.SpawnPriority;
            profile.BarkVoice = humanoid.BarkVoice; // Barks
            profile.OrganMarkings = null; // Shitmed Change: organ-scoped markings no longer exist
            profile.Markings = JsonSerializer.SerializeToDocument(markingStrings);
            profile.HairName = appearance.HairStyleId;
            profile.HairColor = appearance.HairColor.ToHex();
            profile.FacialHairName = appearance.FacialHairStyleId;
            profile.FacialHairColor = appearance.FacialHairColor.ToHex();

            profile.Slot = slot;
            profile.PreferenceUnavailable = (DbPreferenceUnavailableMode) humanoid.PreferenceUnavailable;

            profile.Jobs.Clear();
            profile.Jobs.AddRange(
                humanoid.JobPriorities
                    .Where(j => j.Value != JobPriority.Never)
                    .Select(j => new Job {JobName = j.Key, Priority = (DbJobPriority) j.Value})
            );

            profile.Antags.Clear();
            profile.Antags.AddRange(
                humanoid.AntagPreferences
                    .Select(a => new Antag {AntagName = a})
            );

            profile.Traits.Clear();
            profile.Traits.AddRange(
                humanoid.TraitPreferences
                        .Select(t => new Trait {TraitName = t})
            );

            profile.Loadouts.Clear();

            foreach (var (role, loadouts) in humanoid.Loadouts)
            {
                var dz = new ProfileRoleLoadout()
                {
                    RoleName = role,
                    EntityName = loadouts.EntityName ?? string.Empty,
                };

                foreach (var (group, groupLoadouts) in loadouts.SelectedLoadouts)
                {
                    var profileGroup = new ProfileLoadoutGroup()
                    {
                        GroupName = group,
                    };

                    foreach (var loadout in groupLoadouts)
                    {
                        profileGroup.Loadouts.Add(new ProfileLoadout()
                        {
                            LoadoutName = loadout.Prototype,
                        });
                    }

                    dz.Groups.Add(profileGroup);
                }

                profile.Loadouts.Add(dz);
            }

            return profile;
        }
        #endregion

        #region User Ids
        public async Task<NetUserId?> GetAssignedUserIdAsync(string name)
        {
            await using var db = await GetDb();

            var assigned = await db.DbContext.AssignedUserId.SingleOrDefaultAsync(p => p.UserName == name);
            return assigned?.UserId is { } g ? new NetUserId(g) : default(NetUserId?);
        }

        public async Task AssignUserIdAsync(string name, NetUserId netUserId)
        {
            await using var db = await GetDb();

            db.DbContext.AssignedUserId.Add(new AssignedUserId
            {
                UserId = netUserId.UserId,
                UserName = name
            });

            await db.DbContext.SaveChangesAsync();
        }
        #endregion

        #region Bans
        /*
         * BAN STUFF
         */
        /// <summary>
        ///     Looks up a ban by id.
        ///     This will return a pardoned ban as well.
        /// </summary>
        /// <param name="id">The ban id to look for.</param>
        /// <returns>The ban with the given id or null if none exist.</returns>
        public abstract Task<BanDef?> GetBanAsync(int id);

        /// <summary>
        ///     Looks up an user's most recent received un-pardoned ban.
        ///     This will NOT return a pardoned ban.
        ///     One of <see cref="address"/> or <see cref="userId"/> need to not be null.
        /// </summary>
        /// <param name="address">The ip address of the user.</param>
        /// <param name="userId">The id of the user.</param>
        /// <param name="hwId">The legacy HWId of the user.</param>
        /// <param name="modernHWIds">The modern HWIDs of the user.</param>
        /// <returns>The user's latest received un-pardoned ban, or null if none exist.</returns>
        public abstract Task<BanDef?> GetBanAsync(
            IPAddress? address,
            NetUserId? userId,
            ImmutableArray<byte>? hwId,
            ImmutableArray<ImmutableArray<byte>>? modernHWIds,
            BanType type);

        /// <summary>
        ///     Looks up an user's ban history.
        ///     This will return pardoned bans as well.
        ///     One of <see cref="address"/> or <see cref="userId"/> need to not be null.
        /// </summary>
        /// <param name="address">The ip address of the user.</param>
        /// <param name="userId">The id of the user.</param>
        /// <param name="hwId">The legacy HWId of the user.</param>
        /// <param name="modernHWIds">The modern HWIDs of the user.</param>
        /// <param name="includeUnbanned">Include pardoned and expired bans.</param>
        /// <returns>The user's ban history.</returns>
        public abstract Task<List<BanDef>> GetBansAsync(
            IPAddress? address,
            NetUserId? userId,
            ImmutableArray<byte>? hwId,
            ImmutableArray<ImmutableArray<byte>>? modernHWIds,
            bool includeUnbanned,
            BanType type);

        public abstract Task<BanDef> AddBanAsync(BanDef ban);
        public abstract Task AddUnbanAsync(UnbanDef unban);

        public async Task EditBan(int id, string reason, NoteSeverity severity, DateTimeOffset? expiration, Guid editedBy, DateTimeOffset editedAt)
        {
            await using var db = await GetDb();

            var ban = await db.DbContext.Ban.SingleOrDefaultAsync(b => b.Id == id);
            if (ban is null)
                return;
            ban.Severity = severity;
            ban.Reason = reason;
            ban.ExpirationTime = expiration?.UtcDateTime;
            ban.LastEditedById = editedBy;
            ban.LastEditedAt = editedAt.UtcDateTime;
            await db.DbContext.SaveChangesAsync();
        }

        protected static async Task<ServerBanExemptFlags?> GetBanExemptionCore(
            DbGuard db,
            NetUserId? userId,
            CancellationToken cancel = default)
        {
            if (userId == null)
                return null;

            var exemption = await db.DbContext.BanExemption
                .SingleOrDefaultAsync(e => e.UserId == userId.Value.UserId, cancellationToken: cancel);

            return exemption?.Flags;
        }

        public async Task UpdateBanExemption(NetUserId userId, ServerBanExemptFlags flags)
        {
            await using var db = await GetDb();

            if (flags == 0)
            {
                // Delete whatever is there.
                await db.DbContext.BanExemption.Where(u => u.UserId == userId.UserId).ExecuteDeleteAsync();
                return;
            }

            var exemption = await db.DbContext.BanExemption.SingleOrDefaultAsync(u => u.UserId == userId.UserId);
            if (exemption == null)
            {
                exemption = new ServerBanExemption
                {
                    UserId = userId
                };

                db.DbContext.BanExemption.Add(exemption);
            }

            exemption.Flags = flags;
            await db.DbContext.SaveChangesAsync();
        }

        public async Task<ServerBanExemptFlags> GetBanExemption(NetUserId userId, CancellationToken cancel)
        {
            await using var db = await GetDb(cancel);

            var flags = await GetBanExemptionCore(db, userId, cancel);
            return flags ?? ServerBanExemptFlags.None;
        }

        protected static List<Expression<Func<Ban, object>>> GetBanDefIncludes(BanType? type = null)
        {
            List<Expression<Func<Ban, object>>> list =
            [
                b => b.Players!,
                b => b.Rounds!,
                b => b.Hwids!,
                b => b.Unban!,
                b => b.Addresses!,
            ];

            if (type != BanType.Server)
                list.Add(b => b.Roles!);

            return list;
        }

        #endregion

        #region Playtime
        public async Task<List<PlayTime>> GetPlayTimes(Guid player, CancellationToken cancel)
        {
            await using var db = await GetDb(cancel);

            return await db.DbContext.PlayTime
                .Where(p => p.PlayerId == player)
                .ToListAsync(cancel);
        }

        public async Task UpdatePlayTimes(IReadOnlyCollection<PlayTimeUpdate> updates)
        {
            await using var db = await GetDb();

            // Ideally I would just be able to send a bunch of UPSERT commands, but EFCore is a pile of garbage.
            // So... In the interest of not making this take forever at high update counts...
            // Bulk-load play time objects for all players involved.
            // This allows us to semi-efficiently load all entities we need in a single DB query.
            // Then we can update & insert without further round-trips to the DB.

            var players = updates.Select(u => u.User.UserId).Distinct().ToArray();
            var dbTimes = (await db.DbContext.PlayTime
                    .Where(p => players.Contains(p.PlayerId))
                    .ToArrayAsync())
                .GroupBy(p => p.PlayerId)
                .ToDictionary(g => g.Key, g => g.ToDictionary(p => p.Tracker, p => p));

            foreach (var (user, tracker, time) in updates)
            {
                if (dbTimes.TryGetValue(user.UserId, out var userTimes)
                    && userTimes.TryGetValue(tracker, out var ent))
                {
                    // Already have a tracker in the database, update it.
                    ent.TimeSpent = time;
                    continue;
                }

                // No tracker, make a new one.
                var playTime = new PlayTime
                {
                    Tracker = tracker,
                    PlayerId = user.UserId,
                    TimeSpent = time
                };

                db.DbContext.PlayTime.Add(playTime);
            }

            await db.DbContext.SaveChangesAsync();
        }

        #endregion

        #region Player Records
        /*
         * PLAYER RECORDS
         */
        public async Task UpdatePlayerRecord(
            NetUserId userId,
            string userName,
            IPAddress address,
            ImmutableTypedHwid? hwId)
        {
            await using var db = await GetDb();

            var record = await db.DbContext.Player.SingleOrDefaultAsync(p => p.UserId == userId.UserId);
            if (record == null)
            {
                db.DbContext.Player.Add(record = new Player
                {
                    FirstSeenTime = DateTime.UtcNow,
                    UserId = userId.UserId,
                });
            }

            record.LastSeenTime = DateTime.UtcNow;
            record.LastSeenAddress = address;
            record.LastSeenUserName = userName;
            record.LastSeenHWId = hwId;

            await db.DbContext.SaveChangesAsync();
        }

        public async Task<PlayerRecord?> GetPlayerRecordByUserName(string userName, CancellationToken cancel)
        {
            await using var db = await GetDb();

            // Sort by descending last seen time.
            // So if, due to account renames, we have two people with the same username in the DB,
            // the most recent one is picked.
            var record = await db.DbContext.Player
                .OrderByDescending(p => p.LastSeenTime)
                .FirstOrDefaultAsync(p => p.LastSeenUserName == userName, cancel);

            return record == null ? null : MakePlayerRecord(record);
        }

        public async Task<PlayerRecord?> GetPlayerRecordByUserId(NetUserId userId, CancellationToken cancel)
        {
            await using var db = await GetDb();

            var record = await db.DbContext.Player
                .SingleOrDefaultAsync(p => p.UserId == userId.UserId, cancel);

            return record == null ? null : MakePlayerRecord(record);
        }

        protected async Task<bool> PlayerRecordExists(DbGuard db, NetUserId userId)
        {
            return await db.DbContext.Player.AnyAsync(p => p.UserId == userId);
        }

        [return: NotNullIfNotNull(nameof(player))]
        protected PlayerRecord? MakePlayerRecord(Player? player)
        {
            if (player == null)
                return null;

            return MakePlayerRecord(player.UserId, player);
        }

        protected PlayerRecord MakePlayerRecord(Guid userId, Player? player)
        {
            if (player == null)
            {
                // We don't have a record for this player in the database.
                // This is possible, for example, when banning people that never connected to the server.
                // Just return fallback data here, I guess.
                return new PlayerRecord(new NetUserId(userId), default, userId.ToString(), default, null, null);
            }

            return new PlayerRecord(
                new NetUserId(player.UserId),
                new DateTimeOffset(NormalizeDatabaseTime(player.FirstSeenTime)),
                player.LastSeenUserName,
                new DateTimeOffset(NormalizeDatabaseTime(player.LastSeenTime)),
                player.LastSeenAddress,
                player.LastSeenHWId);
        }

        #endregion

        #region Player Records Browser

        /// <summary>
        /// All player records matching <paramref name="userName"/> exactly, newest first.
        /// Used by the auto-migration path to find candidate "old" accounts (the same name can map to
        /// several GUIDs after re-registrations).
        /// </summary>
        public async Task<List<PlayerRecord>> GetPlayerRecordsByUserNameAsync(string userName, CancellationToken cancel)
        {
            await using var db = await GetDb(cancel);

            var records = await db.DbContext.Player
                .Where(p => p.LastSeenUserName == userName)
                .OrderByDescending(p => p.LastSeenTime)
                .ToListAsync(cancel);

            return records.Select(r => MakePlayerRecord(r)).ToList();
        }

        /// <summary>
        /// A page of player records (newest seen first) enriched with playtime/ban/migration info for the
        /// <c>playerrecords</c> admin browser. <paramref name="filter"/> matches a username substring, or a
        /// full GUID when it parses as one.
        /// </summary>
        public async Task<List<PlayerRecordInfo>> GetPlayerRecordsInfoAsync(
            string? filter,
            int limit,
            int offset,
            CancellationToken cancel)
        {
            await using var db = await GetDb(cancel);

            IQueryable<Player> query = db.DbContext.Player;
            if (!string.IsNullOrWhiteSpace(filter))
            {
                var f = filter.Trim();
                if (Guid.TryParse(f, out var guid))
                    query = query.Where(p => p.UserId == guid);
                else
                    query = query.Where(p => EF.Functions.Like(p.LastSeenUserName, "%" + f + "%"));
            }

            var players = await query
                .OrderByDescending(p => p.LastSeenTime)
                .Skip(offset)
                .Take(limit)
                .ToListAsync(cancel);

            var ids = players.Select(p => p.UserId).ToList();
            var overall = (string) PlayTimeTrackingShared.TrackerOverall;

            var playTimes = await db.DbContext.PlayTime
                .Where(pt => ids.Contains(pt.PlayerId) && pt.Tracker == overall)
                .ToDictionaryAsync(pt => pt.PlayerId, pt => pt.TimeSpent, cancel);

            var banCounts = (await db.DbContext.BanPlayer
                    .Where(bp => ids.Contains(bp.UserId))
                    .GroupBy(bp => bp.UserId)
                    .Select(g => new { g.Key, Count = g.Count() })
                    .ToListAsync(cancel))
                .ToDictionary(x => x.Key, x => x.Count);

            var migrations = await db.DbContext.MigrationLog
                .Where(m => m.Status == MigrationStatus.Completed
                            && (ids.Contains(m.SourceUserId) || ids.Contains(m.TargetUserId)))
                .ToListAsync(cancel);

            // For a record that received data, the source's name; for one whose data moved away, the target's.
            var receivedFrom = migrations
                .GroupBy(m => m.TargetUserId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.Time).First().SourceUserName);
            var movedTo = migrations
                .GroupBy(m => m.SourceUserId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.Time).First().TargetUserName);

            return players.Select(p => new PlayerRecordInfo(
                MakePlayerRecord(p),
                playTimes.GetValueOrDefault(p.UserId),
                banCounts.GetValueOrDefault(p.UserId),
                receivedFrom.GetValueOrDefault(p.UserId),
                movedTo.GetValueOrDefault(p.UserId))).ToList();
        }

        #endregion

        #region Economy (iss14)

        public async Task<long> GetBankBalanceAsync(NetUserId userId, CancellationToken cancel)
        {
            await using var db = await GetDb(cancel);

            var account = await db.DbContext.BankAccount
                .AsNoTracking()
                .SingleOrDefaultAsync(b => b.UserId == userId.UserId, cancel);

            return account?.Balance ?? 0;
        }

        public async Task SetBankBalanceAsync(NetUserId userId, long balance)
        {
            await using var db = await GetDb();

            var account = await GetOrCreateBankAccount(db, userId);
            account.Balance = Math.Max(0, balance);

            await db.DbContext.SaveChangesAsync();
        }

        public async Task<long?> AdjustBankBalanceAsync(NetUserId userId, long delta, bool allowNegative, long maxBalance, bool salary)
        {
            await using var db = await GetDb();

            // The row is loaded and saved inside one transaction; concurrent writers for the same user are
            // additionally serialized by BankManager's per-user queue, so no lost updates.
            await using var tx = await db.DbContext.Database.BeginTransactionAsync();

            var account = await GetOrCreateBankAccount(db, userId);
            var next = account.Balance + delta;

            if (next < 0 && !allowNegative)
                return null;

            if (maxBalance > 0 && next > maxBalance)
                next = maxBalance;

            if (delta > 0)
                account.LifetimeEarned += next - account.Balance;

            if (salary)
                account.LastSalaryPayout = DateTime.UtcNow;

            account.Balance = next;

            await db.DbContext.SaveChangesAsync();
            await tx.CommitAsync();

            return next;
        }

        public async Task<List<BankTopEntry>> GetTopBankAccountsAsync(int count, CancellationToken cancel)
        {
            await using var db = await GetDb(cancel);

            var rows = await db.DbContext.BankAccount
                .AsNoTracking()
                .OrderByDescending(b => b.Balance)
                .Take(count)
                .ToListAsync(cancel);

            var ids = rows.Select(r => r.UserId).ToList();
            var names = await db.DbContext.Player
                .AsNoTracking()
                .Where(p => ids.Contains(p.UserId))
                .Select(p => new { p.UserId, p.LastSeenUserName })
                .ToDictionaryAsync(p => p.UserId, p => p.LastSeenUserName, cancel);

            return rows
                .Select(r => new BankTopEntry(new NetUserId(r.UserId), names.GetValueOrDefault(r.UserId), r.Balance, r.LifetimeEarned))
                .ToList();
        }

        private static async Task<BankAccount> GetOrCreateBankAccount(DbGuard db, NetUserId userId)
        {
            var account = await db.DbContext.BankAccount.SingleOrDefaultAsync(b => b.UserId == userId.UserId);
            if (account != null)
                return account;

            account = new BankAccount { UserId = userId.UserId };
            db.DbContext.BankAccount.Add(account);
            return account;
        }

        #endregion

        #region User Migration

        public async Task<List<MigrationLog>> GetMigrationLogsAsync(int limit, CancellationToken cancel)
        {
            await using var db = await GetDb(cancel);

            return await db.DbContext.MigrationLog
                .OrderByDescending(m => m.Time)
                .Take(limit)
                .ToListAsync(cancel);
        }

        public async Task<MigrationLog?> GetMigrationLogAsync(int id, CancellationToken cancel)
        {
            await using var db = await GetDb(cancel);

            return await db.DbContext.MigrationLog.SingleOrDefaultAsync(m => m.Id == id, cancel);
        }

        public async Task<int> AddMigrationLogAsync(MigrationLog log)
        {
            await using var db = await GetDb();

            db.DbContext.MigrationLog.Add(log);
            await db.DbContext.SaveChangesAsync();

            return log.Id;
        }

        public async Task UpdateMigrationLogStatusAsync(int id, MigrationStatus status, Guid? performedBy, string? detail)
        {
            await using var db = await GetDb();

            var log = await db.DbContext.MigrationLog.SingleOrDefaultAsync(m => m.Id == id);
            if (log == null)
                return;

            log.Status = status;
            if (performedBy != null)
                log.PerformedByUserId = performedBy;
            if (detail != null)
                log.Detail = detail;

            await db.DbContext.SaveChangesAsync();
        }

        /// <summary>
        /// True if a completed migration has already moved <paramref name="source"/>'s data away. Lets the
        /// auto path skip accounts that were already migrated (avoids re-processing the same old GUID).
        /// </summary>
        public async Task<bool> IsCompletedMigrationSourceAsync(Guid source, CancellationToken cancel)
        {
            await using var db = await GetDb(cancel);

            return await db.DbContext.MigrationLog
                .AnyAsync(m => m.SourceUserId == source && m.Status == MigrationStatus.Completed, cancel);
        }

        /// <summary>
        /// True if any migration log entry (in any state) already links this source to this target. Used to
        /// avoid spamming a new pending entry/alert every time a name-only match reconnects.
        /// </summary>
        public async Task<bool> MigrationExistsAsync(Guid source, Guid target, CancellationToken cancel)
        {
            await using var db = await GetDb(cancel);

            return await db.DbContext.MigrationLog
                .AnyAsync(m => m.SourceUserId == source && m.TargetUserId == target, cancel);
        }

        /// <summary>
        /// Re-points the selected groups of per-user data from <paramref name="source"/> onto
        /// <paramref name="target"/> in a single transaction. Returns a human-readable per-table summary.
        /// The source's (now-empty) Player row is intentionally kept as a tombstone; the
        /// <c>migration_log</c> entry prevents it being processed again.
        /// </summary>
        /// <param name="merge">
        /// When true, the target's existing uniquely-keyed data is kept and the source's is combined in
        /// (characters from both accounts, play times summed per tracker, whitelists unioned). When false
        /// ("old replaces new"), the target's existing rows for those groups are dropped first. History-type
        /// data (bans, notes, connection logs) is always merged regardless of this flag.
        /// </param>
        public async Task<string> MigrateUserDataAsync(Guid source, Guid target, MigrationScope scope, bool merge, CancellationToken cancel)
        {
            await using var db = await GetDb(cancel);
            await using var tx = await db.DbContext.Database.BeginTransactionAsync(cancel);
            var ctx = db.DbContext;
            var summary = new List<string>();

            async Task Count(string label, Task<int> op)
            {
                var n = await op;
                if (n > 0)
                    summary.Add($"{label}: {n}");
            }

            if ((scope & MigrationScope.Gameplay) != 0)
            {
                if (merge)
                {
                    // Keep both accounts' characters and sum play times per tracker.
                    await MergePreferences(ctx, source, target, summary, cancel);
                    await MergePlayTimes(ctx, source, target, summary, cancel);
                }
                else
                {
                    // Preferences (unique per user): drop target's, then re-point source's. Profiles cascade.
                    await Count("preferences",
                        ctx.Preference.Where(p => p.UserId == target).ExecuteDeleteAsync(cancel));
                    await Count("preferences-moved",
                        ctx.Preference.Where(p => p.UserId == source)
                            .ExecuteUpdateAsync(s => s.SetProperty(p => p.UserId, target), cancel));

                    // Play times (unique per user+tracker): drop target's, then re-point source's.
                    await ctx.PlayTime.Where(pt => pt.PlayerId == target).ExecuteDeleteAsync(cancel);
                    await Count("playtimes",
                        ctx.PlayTime.Where(pt => pt.PlayerId == source)
                            .ExecuteUpdateAsync(s => s.SetProperty(pt => pt.PlayerId, target), cancel));
                }

                // History (no harmful uniqueness): always merged onto target.
                await Count("connections",
                    ctx.ConnectionLog.Where(c => c.UserId == source)
                        .ExecuteUpdateAsync(s => s.SetProperty(c => c.UserId, target), cancel));
                await Count("uploads",
                    ctx.UploadedResourceLog.Where(u => u.UserId == source)
                        .ExecuteUpdateAsync(s => s.SetProperty(u => u.UserId, target), cancel));
                await Count("admin-log-refs",
                    ctx.AdminLogPlayer.Where(a => a.PlayerUserId == source)
                        .ExecuteUpdateAsync(s => s.SetProperty(a => a.PlayerUserId, target), cancel));
            }

            if ((scope & MigrationScope.Whitelists) != 0)
            {
                if (merge)
                {
                    await MergePkGuidFlag(ctx, ctx.Whitelist, source, target,
                        () => new Whitelist { UserId = target }, summary, "whitelist", cancel);
                    await MergeRoleWhitelists(ctx, source, target, summary, cancel);
                }
                else
                {
                    await ReplacePkGuidRow(ctx, ctx.Whitelist, source, target,
                        src => new Whitelist { UserId = target }, summary, "whitelist", cancel);

                    // Role/job whitelists (unique per user+role): drop target's, then re-point source's.
                    await ctx.RoleWhitelists.Where(w => w.PlayerUserId == target).ExecuteDeleteAsync(cancel);
                    await Count("role-whitelists",
                        ctx.RoleWhitelists.Where(w => w.PlayerUserId == source)
                            .ExecuteUpdateAsync(s => s.SetProperty(w => w.PlayerUserId, target), cancel));
                }
            }

            if ((scope & MigrationScope.Bans) != 0)
            {
                // Bans follow the person (anti-evasion): always merged, skipping any ban the target is on.
                var targetBanIds = await ctx.BanPlayer.Where(bp => bp.UserId == target)
                    .Select(bp => bp.BanId).ToListAsync(cancel);
                await ctx.BanPlayer.Where(bp => bp.UserId == source && targetBanIds.Contains(bp.BanId))
                    .ExecuteDeleteAsync(cancel);
                await Count("bans",
                    ctx.BanPlayer.Where(bp => bp.UserId == source)
                        .ExecuteUpdateAsync(s => s.SetProperty(bp => bp.UserId, target), cancel));

                if (merge)
                {
                    await MergePkGuidFlag(ctx, ctx.Blacklist, source, target,
                        () => new Blacklist { UserId = target }, summary, "blacklist", cancel);
                    await MergeBanExemption(ctx, source, target, summary, cancel);
                }
                else
                {
                    await ReplacePkGuidRow(ctx, ctx.Blacklist, source, target,
                        src => new Blacklist { UserId = target }, summary, "blacklist", cancel);
                    await ReplacePkGuidRow(ctx, ctx.BanExemption, source, target,
                        src => new ServerBanExemption { UserId = target, Flags = src.Flags }, summary, "ban-exemption", cancel);
                }

                // Admin remarks received by the player: merge onto target.
                await Count("notes",
                    ctx.AdminNotes.Where(n => n.PlayerUserId == source)
                        .ExecuteUpdateAsync(s => s.SetProperty(n => n.PlayerUserId, target), cancel));
                await Count("watchlists",
                    ctx.AdminWatchlists.Where(n => n.PlayerUserId == source)
                        .ExecuteUpdateAsync(s => s.SetProperty(n => n.PlayerUserId, target), cancel));
                await Count("messages",
                    ctx.AdminMessages.Where(n => n.PlayerUserId == source)
                        .ExecuteUpdateAsync(s => s.SetProperty(n => n.PlayerUserId, target), cancel));
            }

            if ((scope & MigrationScope.Admin) != 0)
            {
                await MigrateAdminStatus(ctx, source, target, merge, summary, cancel);

                // Authorship of admin actions follows the admin's new identity.
                await ctx.AdminNotes.Where(n => n.CreatedById == source)
                    .ExecuteUpdateAsync(s => s.SetProperty(n => n.CreatedById, target), cancel);
                await ctx.AdminNotes.Where(n => n.LastEditedById == source)
                    .ExecuteUpdateAsync(s => s.SetProperty(n => n.LastEditedById, target), cancel);
                await ctx.AdminNotes.Where(n => n.DeletedById == source)
                    .ExecuteUpdateAsync(s => s.SetProperty(n => n.DeletedById, target), cancel);
                await ctx.AdminWatchlists.Where(n => n.CreatedById == source)
                    .ExecuteUpdateAsync(s => s.SetProperty(n => n.CreatedById, target), cancel);
                await ctx.AdminWatchlists.Where(n => n.LastEditedById == source)
                    .ExecuteUpdateAsync(s => s.SetProperty(n => n.LastEditedById, target), cancel);
                await ctx.AdminMessages.Where(n => n.CreatedById == source)
                    .ExecuteUpdateAsync(s => s.SetProperty(n => n.CreatedById, target), cancel);
                await ctx.AdminMessages.Where(n => n.LastEditedById == source)
                    .ExecuteUpdateAsync(s => s.SetProperty(n => n.LastEditedById, target), cancel);
                await ctx.Ban.Where(b => b.BanningAdmin == source)
                    .ExecuteUpdateAsync(s => s.SetProperty(b => b.BanningAdmin, target), cancel);
                await ctx.Ban.Where(b => b.LastEditedById == source)
                    .ExecuteUpdateAsync(s => s.SetProperty(b => b.LastEditedById, target), cancel);
            }

            // Preserve the earliest first-seen on the surviving (target) record.
            var src = await ctx.Player.SingleOrDefaultAsync(p => p.UserId == source, cancel);
            var tgt = await ctx.Player.SingleOrDefaultAsync(p => p.UserId == target, cancel);
            if (src != null && tgt != null && src.FirstSeenTime < tgt.FirstSeenTime)
            {
                tgt.FirstSeenTime = src.FirstSeenTime;
                await ctx.SaveChangesAsync(cancel);
            }

            await tx.CommitAsync(cancel);

            var mode = merge ? "merge" : "replace";
            return summary.Count == 0 ? $"{mode}: nothing to move" : $"{mode}: {string.Join(", ", summary)}";
        }

        /// <summary>
        /// "Old replaces new" for a table whose primary key is the user's GUID: drop the target's row (if
        /// any) and, when the source has a row, replace it with an equivalent row under the target GUID.
        /// </summary>
        private static async Task ReplacePkGuidRow<T>(
            ServerDbContext ctx,
            DbSet<T> set,
            Guid source,
            Guid target,
            Func<T, T> clone,
            List<string> summary,
            string label,
            CancellationToken cancel) where T : class
        {
            var srcRow = await set.SingleOrDefaultAsync(BuildUserIdPredicate<T>(source), cancel);
            await set.Where(BuildUserIdPredicate<T>(target)).ExecuteDeleteAsync(cancel);
            if (srcRow == null)
                return;

            set.Remove(srcRow);
            await ctx.SaveChangesAsync(cancel);
            set.Add(clone(srcRow));
            await ctx.SaveChangesAsync(cancel);
            summary.Add(label);
        }

        private static Expression<Func<T, bool>> BuildUserIdPredicate<T>(Guid userId)
        {
            var param = Expression.Parameter(typeof(T), "e");
            var body = Expression.Equal(Expression.Property(param, "UserId"), Expression.Constant(userId));
            return Expression.Lambda<Func<T, bool>>(body, param);
        }

        private static async Task MigrateAdminStatus(
            ServerDbContext ctx,
            Guid source,
            Guid target,
            bool merge,
            List<string> summary,
            CancellationToken cancel)
        {
            var srcAdmin = await ctx.Admin.Include(a => a.Flags).SingleOrDefaultAsync(a => a.UserId == source, cancel);
            var tgtAdmin = await ctx.Admin.Include(a => a.Flags).SingleOrDefaultAsync(a => a.UserId == target, cancel);

            // Admin status can't meaningfully be combined (one rank per account). In merge mode, keep the
            // target's existing admin if it has one and just drop the source's; otherwise adopt the source's.
            if (merge && tgtAdmin != null)
            {
                if (srcAdmin != null)
                {
                    ctx.Admin.Remove(srcAdmin);
                    await ctx.SaveChangesAsync(cancel);
                }
                summary.Add("admin-kept-target");
                return;
            }

            if (tgtAdmin != null)
                ctx.Admin.Remove(tgtAdmin);
            if (srcAdmin != null)
                ctx.Admin.Remove(srcAdmin);
            await ctx.SaveChangesAsync(cancel);

            if (srcAdmin == null)
                return;

            ctx.Admin.Add(new Admin
            {
                UserId = target,
                Title = srcAdmin.Title,
                AdminRankId = srcAdmin.AdminRankId,
                Deadminned = srcAdmin.Deadminned,
                Suspended = srcAdmin.Suspended,
                Flags = srcAdmin.Flags.Select(f => new AdminFlag { Flag = f.Flag, Negative = f.Negative }).ToList(),
            });
            await ctx.SaveChangesAsync(cancel);
            summary.Add("admin");
        }

        /// <summary>
        /// Merge characters: keep the target's preferences and append every source character after the
        /// target's existing slots, so both accounts' characters survive. The emptied source preference row
        /// is then removed.
        /// </summary>
        private static async Task MergePreferences(
            ServerDbContext ctx,
            Guid source,
            Guid target,
            List<string> summary,
            CancellationToken cancel)
        {
            var srcPref = await ctx.Preference.Include(p => p.Profiles)
                .SingleOrDefaultAsync(p => p.UserId == source, cancel);
            if (srcPref == null)
                return;

            var tgtPref = await ctx.Preference.Include(p => p.Profiles)
                .SingleOrDefaultAsync(p => p.UserId == target, cancel);

            // Target has no preferences yet: just adopt the source's wholesale.
            if (tgtPref == null)
            {
                srcPref.UserId = target;
                await ctx.SaveChangesAsync(cancel);
                summary.Add($"characters: {srcPref.Profiles.Count}");
                return;
            }

            var nextSlot = tgtPref.Profiles.Count == 0 ? 0 : tgtPref.Profiles.Max(p => p.Slot) + 1;
            var moved = 0;
            foreach (var profile in srcPref.Profiles.OrderBy(p => p.Slot).ToList())
            {
                // Re-parent the character onto the target's preference at a fresh slot. Its Jobs/Antags/
                // Traits/Loadouts are keyed by ProfileId (unchanged), so they follow automatically.
                profile.Slot = nextSlot++;
                profile.PreferenceId = tgtPref.Id;
                profile.Preference = tgtPref;
                tgtPref.Profiles.Add(profile);
                moved++;
            }
            srcPref.Profiles.Clear();
            await ctx.SaveChangesAsync(cancel);

            // The source preference row is now empty; drop it (don't cascade-delete the moved characters).
            ctx.Preference.Remove(srcPref);
            await ctx.SaveChangesAsync(cancel);

            if (moved > 0)
                summary.Add($"characters: {moved}");
        }

        /// <summary>Merge play times: sum the source's time into the target per tracker, keeping the union.</summary>
        private static async Task MergePlayTimes(
            ServerDbContext ctx,
            Guid source,
            Guid target,
            List<string> summary,
            CancellationToken cancel)
        {
            var srcTimes = await ctx.PlayTime.Where(p => p.PlayerId == source).ToListAsync(cancel);
            if (srcTimes.Count == 0)
                return;

            var tgtByTracker = (await ctx.PlayTime.Where(p => p.PlayerId == target).ToListAsync(cancel))
                .ToDictionary(t => t.Tracker);

            foreach (var srcTime in srcTimes)
            {
                if (tgtByTracker.TryGetValue(srcTime.Tracker, out var tgtTime))
                {
                    tgtTime.TimeSpent += srcTime.TimeSpent;
                    ctx.PlayTime.Remove(srcTime);
                }
                else
                {
                    // Target lacks this tracker: re-point the source row (no unique conflict).
                    srcTime.PlayerId = target;
                }
            }

            await ctx.SaveChangesAsync(cancel);
            summary.Add($"playtimes: {srcTimes.Count}");
        }

        /// <summary>Union role/job whitelists onto the target, dropping any the target already has.</summary>
        private static async Task MergeRoleWhitelists(
            ServerDbContext ctx,
            Guid source,
            Guid target,
            List<string> summary,
            CancellationToken cancel)
        {
            var targetRoles = await ctx.RoleWhitelists.Where(w => w.PlayerUserId == target)
                .Select(w => w.RoleId).ToListAsync(cancel);
            await ctx.RoleWhitelists.Where(w => w.PlayerUserId == source && targetRoles.Contains(w.RoleId))
                .ExecuteDeleteAsync(cancel);

            var moved = await ctx.RoleWhitelists.Where(w => w.PlayerUserId == source)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.PlayerUserId, target), cancel);
            if (moved > 0)
                summary.Add($"role-whitelists: {moved}");
        }

        /// <summary>
        /// Union a presence flag keyed by user GUID (whitelist/blacklist): if the source has it, ensure the
        /// target has it too, then remove the source's row. The target's row is never dropped.
        /// </summary>
        private static async Task MergePkGuidFlag<T>(
            ServerDbContext ctx,
            DbSet<T> set,
            Guid source,
            Guid target,
            Func<T> makeTarget,
            List<string> summary,
            string label,
            CancellationToken cancel) where T : class
        {
            if (!await set.AnyAsync(BuildUserIdPredicate<T>(source), cancel))
                return;

            if (!await set.AnyAsync(BuildUserIdPredicate<T>(target), cancel))
                set.Add(makeTarget());

            await set.Where(BuildUserIdPredicate<T>(source)).ExecuteDeleteAsync(cancel);
            await ctx.SaveChangesAsync(cancel);
            summary.Add(label);
        }

        /// <summary>Merge ban exemptions: OR the source's exempt flags into the target's, then drop the source.</summary>
        private static async Task MergeBanExemption(
            ServerDbContext ctx,
            Guid source,
            Guid target,
            List<string> summary,
            CancellationToken cancel)
        {
            var srcEx = await ctx.BanExemption.SingleOrDefaultAsync(e => e.UserId == source, cancel);
            if (srcEx == null)
                return;

            var tgtEx = await ctx.BanExemption.SingleOrDefaultAsync(e => e.UserId == target, cancel);
            if (tgtEx == null)
                ctx.BanExemption.Add(new ServerBanExemption { UserId = target, Flags = srcEx.Flags });
            else
                tgtEx.Flags |= srcEx.Flags;

            ctx.BanExemption.Remove(srcEx);
            await ctx.SaveChangesAsync(cancel);
            summary.Add("ban-exemption");
        }

        #endregion

        #region Connection Logs
        /*
         * CONNECTION LOG
         */
        public abstract Task<int> AddConnectionLogAsync(NetUserId userId,
            string userName,
            IPAddress address,
            ImmutableTypedHwid? hwId,
            float trust,
            ConnectionDenyReason? denied,
            int serverId);

        public async Task AddServerBanHitsAsync(int connection, IEnumerable<BanDef> bans)
        {
            await using var db = await GetDb();

            foreach (var ban in bans)
            {
                db.DbContext.ServerBanHit.Add(new ServerBanHit
                {
                    ConnectionId = connection, BanId = ban.Id!.Value
                });
            }

            await db.DbContext.SaveChangesAsync();
        }

        #endregion

        #region Admin Ranks
        /*
         * ADMIN RANKS
         */
        public async Task<Admin?> GetAdminDataForAsync(NetUserId userId, CancellationToken cancel)
        {
            await using var db = await GetDb(cancel);

            return await db.DbContext.Admin
                .Include(p => p.Flags)
                .Include(p => p.AdminRank)
                .ThenInclude(p => p!.Flags)
                .AsSplitQuery() // tests fail because of a random warning if you dont have this!
                .SingleOrDefaultAsync(p => p.UserId == userId.UserId, cancel);
        }

        public abstract Task<((Admin, string? lastUserName)[] admins, AdminRank[])>
            GetAllAdminAndRanksAsync(CancellationToken cancel);

        public async Task<AdminRank?> GetAdminRankDataForAsync(int id, CancellationToken cancel = default)
        {
            await using var db = await GetDb(cancel);

            return await db.DbContext.AdminRank
                .Include(r => r.Flags)
                .SingleOrDefaultAsync(r => r.Id == id, cancel);
        }

        public async Task RemoveAdminAsync(NetUserId userId, CancellationToken cancel)
        {
            await using var db = await GetDb(cancel);

            var admin = await db.DbContext.Admin.SingleAsync(a => a.UserId == userId.UserId, cancel);
            db.DbContext.Admin.Remove(admin);

            await db.DbContext.SaveChangesAsync(cancel);
        }

        public async Task AddAdminAsync(Admin admin, CancellationToken cancel)
        {
            await using var db = await GetDb(cancel);

            db.DbContext.Admin.Add(admin);

            await db.DbContext.SaveChangesAsync(cancel);
        }

        public async Task UpdateAdminAsync(Admin admin, CancellationToken cancel)
        {
            await using var db = await GetDb(cancel);

            var existing = await db.DbContext.Admin.Include(a => a.Flags).SingleAsync(a => a.UserId == admin.UserId, cancel);
            existing.Flags = admin.Flags;
            existing.Title = admin.Title;
            existing.AdminRankId = admin.AdminRankId;
            existing.Deadminned = admin.Deadminned;
            existing.Suspended = admin.Suspended;

            await db.DbContext.SaveChangesAsync(cancel);
        }

        public async Task UpdateAdminDeadminnedAsync(NetUserId userId, bool deadminned, CancellationToken cancel)
        {
            await using var db = await GetDb(cancel);

            var adminRecord = db.DbContext.Admin.Where(a => a.UserId == userId);
            await adminRecord.ExecuteUpdateAsync(
                set => set.SetProperty(p => p.Deadminned, deadminned),
                cancellationToken: cancel);

            await db.DbContext.SaveChangesAsync(cancel);
        }

        public async Task RemoveAdminRankAsync(int rankId, CancellationToken cancel)
        {
            await using var db = await GetDb(cancel);

            var admin = await db.DbContext.AdminRank.SingleAsync(a => a.Id == rankId, cancel);
            db.DbContext.AdminRank.Remove(admin);

            await db.DbContext.SaveChangesAsync(cancel);
        }

        public async Task AddAdminRankAsync(AdminRank rank, CancellationToken cancel)
        {
            await using var db = await GetDb(cancel);

            db.DbContext.AdminRank.Add(rank);

            await db.DbContext.SaveChangesAsync(cancel);
        }

        public async Task<int> AddNewRound(Server server, params Guid[] playerIds)
        {
            await using var db = await GetDb();

            var players = await db.DbContext.Player
                .Where(player => playerIds.Contains(player.UserId))
                .ToListAsync();

            var round = new Round
            {
                StartDate = DateTime.UtcNow,
                Players = players,
                ServerId = server.Id
            };

            db.DbContext.Round.Add(round);

            await db.DbContext.SaveChangesAsync();

            return round.Id;
        }

        public async Task<Round> GetRound(int id)
        {
            await using var db = await GetDb();

            var round = await db.DbContext.Round
                .Include(round => round.Players)
                .SingleAsync(round => round.Id == id);

            return round;
        }

        public async Task AddRoundPlayers(int id, Guid[] playerIds)
        {
            await using var db = await GetDb();

            // ReSharper disable once SuggestVarOrType_Elsewhere
            Dictionary<Guid, int> players = await db.DbContext.Player
                .Where(player => playerIds.Contains(player.UserId))
                .ToDictionaryAsync(player => player.UserId, player => player.Id);

            foreach (var player in playerIds)
            {
                await db.DbContext.Database.ExecuteSqlAsync($"""
INSERT INTO player_round (players_id, rounds_id) VALUES ({players[player]}, {id}) ON CONFLICT DO NOTHING
""");
            }

            await db.DbContext.SaveChangesAsync();
        }

        [return: NotNullIfNotNull(nameof(round))]
        protected RoundRecord? MakeRoundRecord(Round? round)
        {
            if (round == null)
                return null;

            return new RoundRecord(
                round.Id,
                NormalizeDatabaseTime(round.StartDate),
                MakeServerRecord(round.Server));
        }

        public async Task UpdateAdminRankAsync(AdminRank rank, CancellationToken cancel)
        {
            await using var db = await GetDb(cancel);

            var existing = await db.DbContext.AdminRank
                .Include(r => r.Flags)
                .SingleAsync(a => a.Id == rank.Id, cancel);

            existing.Flags = rank.Flags;
            existing.Name = rank.Name;

            await db.DbContext.SaveChangesAsync(cancel);
        }
        #endregion

        #region Admin Logs

        public async Task<(Server, bool existed)> AddOrGetServer(string serverName)
        {
            await using var db = await GetDb();
            var server = await db.DbContext.Server
                .Where(server => server.Name.Equals(serverName))
                .SingleOrDefaultAsync();

            if (server != default)
                return (server, true);

            server = new Server
            {
                Name = serverName
            };

            db.DbContext.Server.Add(server);

            await db.DbContext.SaveChangesAsync();

            return (server, false);
        }

        [return: NotNullIfNotNull(nameof(server))]
        protected ServerRecord? MakeServerRecord(Server? server)
        {
            if (server == null)
                return null;

            return new ServerRecord(server.Id, server.Name);
        }

        public async Task AddAdminLogs(List<AdminLog> logs)
        {
            const int maxRetryAttempts = 5;
            var initialRetryDelay = TimeSpan.FromSeconds(5);

            DebugTools.Assert(logs.All(x => x.RoundId > 0), "Adding logs with invalid round ids.");

            var attempt = 0;
            var retryDelay = initialRetryDelay;

            while (attempt < maxRetryAttempts)
            {
                try
                {
                    await using var db = await GetDb();
                    db.DbContext.AdminLog.AddRange(logs);
                    await db.DbContext.SaveChangesAsync();
                    _opsLog.Debug($"Successfully saved {logs.Count} admin logs.");
                    break;
                }
                catch (Exception ex)
                {
                    attempt += 1;
                    _opsLog.Error($"Attempt {attempt} failed to save logs: {ex}");

                    if (attempt >= maxRetryAttempts)
                    {
                        _opsLog.Error($"Max retry attempts reached. Failed to save {logs.Count} admin logs.");
                        throw;
                    }

                    _opsLog.Warning($"Retrying in {retryDelay.TotalSeconds} seconds...");
                    await Task.Delay(retryDelay);

                    retryDelay *= 2;
                }
            }
        }

        protected abstract IQueryable<AdminLog> StartAdminLogsQuery(ServerDbContext db, LogFilter? filter = null);

        private IQueryable<AdminLog> GetAdminLogsQuery(ServerDbContext db, LogFilter? filter = null)
        {
            // Save me from SQLite
            var query = StartAdminLogsQuery(db, filter);

            if (filter == null)
            {
                return query.OrderBy(log => log.Date);
            }

            if (filter.Round != null)
            {
                query = query.Where(log => log.RoundId == filter.Round);
            }

            if (filter.Types != null)
            {
                query = query.Where(log => filter.Types.Contains(log.Type));
            }

            if (filter.Impacts != null)
            {
                query = query.Where(log => filter.Impacts.Contains(log.Impact));
            }

            if (filter.Before != null)
            {
                query = query.Where(log => log.Date < filter.Before);
            }

            if (filter.After != null)
            {
                query = query.Where(log => log.Date > filter.After);
            }

            if (filter.IncludePlayers)
            {
                if (filter.AnyPlayers != null)
                {
                    query = query.Where(log =>
                        log.Players.Any(p => filter.AnyPlayers.Contains(p.PlayerUserId)) ||
                        log.Players.Count == 0 && filter.IncludeNonPlayers);
                }

                if (filter.AllPlayers != null)
                {
                    query = query.Where(log =>
                        log.Players.All(p => filter.AllPlayers.Contains(p.PlayerUserId)) ||
                        log.Players.Count == 0 && filter.IncludeNonPlayers);
                }
            }
            else
            {
                query = query.Where(log => log.Players.Count == 0);
            }

            if (filter.LastLogId != null)
            {
                query = filter.DateOrder switch
                {
                    DateOrder.Ascending => query.Where(log => log.Id > filter.LastLogId),
                    DateOrder.Descending => query.Where(log => log.Id < filter.LastLogId),
                    _ => throw new ArgumentOutOfRangeException(nameof(filter),
                        $"Unknown {nameof(DateOrder)} value {filter.DateOrder}")
                };
            }

            query = filter.DateOrder switch
            {
                DateOrder.Ascending => query.OrderBy(log => log.Date),
                DateOrder.Descending => query.OrderByDescending(log => log.Date),
                _ => throw new ArgumentOutOfRangeException(nameof(filter),
                    $"Unknown {nameof(DateOrder)} value {filter.DateOrder}")
            };

            const int hardLogLimit = 500_000;
            if (filter.Limit != null)
            {
                query = query.Take(Math.Min(filter.Limit.Value, hardLogLimit));
            }
            else
            {
                query = query.Take(hardLogLimit);
            }

            return query;
        }

        public async IAsyncEnumerable<string> GetAdminLogMessages(LogFilter? filter = null)
        {
            await using var db = await GetDb();
            var query = GetAdminLogsQuery(db.DbContext, filter);

            await foreach (var log in query.Select(log => log.Message).AsAsyncEnumerable())
            {
                yield return log;
            }
        }

        public async IAsyncEnumerable<SharedAdminLog> GetAdminLogs(LogFilter? filter = null)
        {
            await using var db = await GetDb();
            var query = GetAdminLogsQuery(db.DbContext, filter);
            query = query.Include(log => log.Players);

            await foreach (var log in query.AsAsyncEnumerable())
            {
                var players = new Guid[log.Players.Count];
                for (var i = 0; i < log.Players.Count; i++)
                {
                    players[i] = log.Players[i].PlayerUserId;
                }

                yield return new SharedAdminLog(log.Id, log.Type, log.Impact, log.Date, log.Message, players);
            }
        }

        public async IAsyncEnumerable<JsonDocument> GetAdminLogsJson(LogFilter? filter = null)
        {
            await using var db = await GetDb();
            var query = GetAdminLogsQuery(db.DbContext, filter);

            await foreach (var json in query.Select(log => log.Json).AsAsyncEnumerable())
            {
                yield return json;
            }
        }

        public async Task<int> CountAdminLogs(int round)
        {
            await using var db = await GetDb();
            return await db.DbContext.AdminLog.CountAsync(log => log.RoundId == round);
        }

        #endregion

        #region Whitelist

        public async Task<bool> GetWhitelistStatusAsync(NetUserId player)
        {
            await using var db = await GetDb();

            return await db.DbContext.Whitelist.AnyAsync(w => w.UserId == player);
        }

        public async Task AddToWhitelistAsync(NetUserId player)
        {
            await using var db = await GetDb();

            db.DbContext.Whitelist.Add(new Whitelist { UserId = player });
            await db.DbContext.SaveChangesAsync();
        }

        public async Task RemoveFromWhitelistAsync(NetUserId player)
        {
            await using var db = await GetDb();
            var entry = await db.DbContext.Whitelist.SingleAsync(w => w.UserId == player);
            db.DbContext.Whitelist.Remove(entry);
            await db.DbContext.SaveChangesAsync();
        }

        public async Task<DateTimeOffset?> GetLastReadRules(NetUserId player)
        {
            await using var db = await GetDb();

            return NormalizeDatabaseTime(await db.DbContext.Player
                .Where(dbPlayer => dbPlayer.UserId == player)
                .Select(dbPlayer => dbPlayer.LastReadRules)
                .SingleOrDefaultAsync());
        }

        public async Task SetLastReadRules(NetUserId player, DateTimeOffset? date)
        {
            await using var db = await GetDb();

            var dbPlayer = await db.DbContext.Player.Where(dbPlayer => dbPlayer.UserId == player).SingleOrDefaultAsync();
            if (dbPlayer == null)
            {
                return;
            }

            dbPlayer.LastReadRules = date?.UtcDateTime;
            await db.DbContext.SaveChangesAsync();
        }

        public async Task<bool> GetBlacklistStatusAsync(NetUserId player)
        {
            await using var db = await GetDb();

            return await db.DbContext.Blacklist.AnyAsync(w => w.UserId == player);
        }

        public async Task AddToBlacklistAsync(NetUserId player)
        {
            await using var db = await GetDb();

            db.DbContext.Blacklist.Add(new Blacklist() { UserId = player });
            await db.DbContext.SaveChangesAsync();
        }

        public async Task RemoveFromBlacklistAsync(NetUserId player)
        {
            await using var db = await GetDb();
            var entry = await db.DbContext.Blacklist.SingleAsync(w => w.UserId == player);
            db.DbContext.Blacklist.Remove(entry);
            await db.DbContext.SaveChangesAsync();
        }

        #endregion

        #region Uploaded Resources Logs

        public async Task AddUploadedResourceLogAsync(NetUserId user, DateTimeOffset date, string path, byte[] data)
        {
            await using var db = await GetDb();

            db.DbContext.UploadedResourceLog.Add(new UploadedResourceLog() { UserId = user, Date = date.UtcDateTime, Path = path, Data = data });
            await db.DbContext.SaveChangesAsync();
        }

        public async Task PurgeUploadedResourceLogAsync(int days)
        {
            await using var db = await GetDb();

            var date = DateTime.UtcNow.Subtract(TimeSpan.FromDays(days));

            await foreach (var log in db.DbContext.UploadedResourceLog
                               .Where(l => date > l.Date)
                               .AsAsyncEnumerable())
            {
                db.DbContext.UploadedResourceLog.Remove(log);
            }

            await db.DbContext.SaveChangesAsync();
        }

        #endregion

        #region Admin Notes

        public virtual async Task<int> AddAdminNote(AdminNote note)
        {
            await using var db = await GetDb();
            db.DbContext.AdminNotes.Add(note);
            await db.DbContext.SaveChangesAsync();
            return note.Id;
        }

        public virtual async Task<int> AddAdminWatchlist(AdminWatchlist watchlist)
        {
            await using var db = await GetDb();
            db.DbContext.AdminWatchlists.Add(watchlist);
            await db.DbContext.SaveChangesAsync();
            return watchlist.Id;
        }

        public virtual async Task<int> AddAdminMessage(AdminMessage message)
        {
            await using var db = await GetDb();
            db.DbContext.AdminMessages.Add(message);
            await db.DbContext.SaveChangesAsync();
            return message.Id;
        }

        public async Task<AdminNoteRecord?> GetAdminNote(int id)
        {
            await using var db = await GetDb();
            var entity = await db.DbContext.AdminNotes
                .Where(note => note.Id == id)
                .Include(note => note.Round)
                .ThenInclude(r => r!.Server)
                .Include(note => note.CreatedBy)
                .Include(note => note.LastEditedBy)
                .Include(note => note.DeletedBy)
                .Include(note => note.Player)
                .SingleOrDefaultAsync();

            return entity == null ? null : MakeAdminNoteRecord(entity);
        }

        private AdminNoteRecord MakeAdminNoteRecord(AdminNote entity)
        {
            return new AdminNoteRecord(
                entity.Id,
                MakeRoundRecord(entity.Round),
                MakePlayerRecord(entity.Player),
                entity.PlaytimeAtNote,
                entity.Message,
                entity.Severity,
                MakePlayerRecord(entity.CreatedBy),
                NormalizeDatabaseTime(entity.CreatedAt),
                MakePlayerRecord(entity.LastEditedBy),
                NormalizeDatabaseTime(entity.LastEditedAt),
                NormalizeDatabaseTime(entity.ExpirationTime),
                entity.Deleted,
                MakePlayerRecord(entity.DeletedBy),
                NormalizeDatabaseTime(entity.DeletedAt),
                entity.Secret);
        }

        public async Task<AdminWatchlistRecord?> GetAdminWatchlist(int id)
        {
            await using var db = await GetDb();
            var entity = await db.DbContext.AdminWatchlists
                .Where(note => note.Id == id)
                .Include(note => note.Round)
                .ThenInclude(r => r!.Server)
                .Include(note => note.CreatedBy)
                .Include(note => note.LastEditedBy)
                .Include(note => note.DeletedBy)
                .Include(note => note.Player)
                .SingleOrDefaultAsync();

            return entity == null ? null : MakeAdminWatchlistRecord(entity);
        }

        public async Task<AdminMessageRecord?> GetAdminMessage(int id)
        {
            await using var db = await GetDb();
            var entity = await db.DbContext.AdminMessages
                .Where(note => note.Id == id)
                .Include(note => note.Round)
                .ThenInclude(r => r!.Server)
                .Include(note => note.CreatedBy)
                .Include(note => note.LastEditedBy)
                .Include(note => note.DeletedBy)
                .Include(note => note.Player)
                .SingleOrDefaultAsync();

            return entity == null ? null : MakeAdminMessageRecord(entity);
        }

        private AdminMessageRecord MakeAdminMessageRecord(AdminMessage entity)
        {
            return new AdminMessageRecord(
                entity.Id,
                MakeRoundRecord(entity.Round),
                MakePlayerRecord(entity.Player),
                entity.PlaytimeAtNote,
                entity.Message,
                MakePlayerRecord(entity.CreatedBy),
                NormalizeDatabaseTime(entity.CreatedAt),
                MakePlayerRecord(entity.LastEditedBy),
                NormalizeDatabaseTime(entity.LastEditedAt),
                NormalizeDatabaseTime(entity.ExpirationTime),
                entity.Deleted,
                MakePlayerRecord(entity.DeletedBy),
                NormalizeDatabaseTime(entity.DeletedAt),
                entity.Seen,
                entity.Dismissed);
        }

        public async Task<BanNoteRecord?> GetBanAsNoteAsync(int id)
        {
            await using var db = await GetDb();

            var ban = await BanRecordQuery(db.DbContext)
                .SingleOrDefaultAsync(b => b.Id == id);

            if (ban is null)
                return null;

            return await MakeBanNoteRecord(db.DbContext, ban);
        }

        public async Task<List<IAdminRemarksRecord>> GetAllAdminRemarks(Guid player)
        {
            return await ParallelCollect<IAdminRemarksRecord>(
                async () =>
                {
                    await using var db = await GetDb();
                    return (await (from note in db.DbContext.AdminNotes
                            where note.PlayerUserId == player &&
                                  !note.Deleted &&
                                  (note.ExpirationTime == null || DateTime.UtcNow < note.ExpirationTime)
                            select note)
                        .Include(note => note.Round)
                        .ThenInclude(r => r!.Server)
                        .Include(note => note.CreatedBy)
                        .Include(note => note.LastEditedBy)
                        .Include(note => note.Player)
                        .ToListAsync()).Select(MakeAdminNoteRecord);
                },
                async () =>
                {
                    await using var db = await GetDb();
                    return await GetActiveWatchlistsImpl(db, player);
                },
                async () =>
                {
                    await using var db = await GetDb();
                    return await GetMessagesImpl(db, player);
                },
                async () =>
                {
                    await using var db = await GetDb();
                    return await GetBansAsNotesForUser(db, player);
                });
        }
        public async Task EditAdminNote(int id, string message, NoteSeverity severity, bool secret, Guid editedBy, DateTimeOffset editedAt, DateTimeOffset? expiryTime)
        {
            await using var db = await GetDb();

            var note = await db.DbContext.AdminNotes.Where(note => note.Id == id).SingleAsync();
            note.Message = message;
            note.Severity = severity;
            note.Secret = secret;
            note.LastEditedById = editedBy;
            note.LastEditedAt = editedAt.UtcDateTime;
            note.ExpirationTime = expiryTime?.UtcDateTime;

            await db.DbContext.SaveChangesAsync();
        }

        public async Task EditAdminWatchlist(int id, string message, Guid editedBy, DateTimeOffset editedAt, DateTimeOffset? expiryTime)
        {
            await using var db = await GetDb();

            var note = await db.DbContext.AdminWatchlists.Where(note => note.Id == id).SingleAsync();
            note.Message = message;
            note.LastEditedById = editedBy;
            note.LastEditedAt = editedAt.UtcDateTime;
            note.ExpirationTime = expiryTime?.UtcDateTime;

            await db.DbContext.SaveChangesAsync();
        }

        public async Task EditAdminMessage(int id, string message, Guid editedBy, DateTimeOffset editedAt, DateTimeOffset? expiryTime)
        {
            await using var db = await GetDb();

            var note = await db.DbContext.AdminMessages.Where(note => note.Id == id).SingleAsync();
            note.Message = message;
            note.LastEditedById = editedBy;
            note.LastEditedAt = editedAt.UtcDateTime;
            note.ExpirationTime = expiryTime?.UtcDateTime;

            await db.DbContext.SaveChangesAsync();
        }

        public async Task DeleteAdminNote(int id, Guid deletedBy, DateTimeOffset deletedAt)
        {
            await using var db = await GetDb();

            var note = await db.DbContext.AdminNotes.Where(note => note.Id == id).SingleAsync();

            note.Deleted = true;
            note.DeletedById = deletedBy;
            note.DeletedAt = deletedAt.UtcDateTime;

            await db.DbContext.SaveChangesAsync();
        }

        public async Task DeleteAdminWatchlist(int id, Guid deletedBy, DateTimeOffset deletedAt)
        {
            await using var db = await GetDb();

            var watchlist = await db.DbContext.AdminWatchlists.Where(note => note.Id == id).SingleAsync();

            watchlist.Deleted = true;
            watchlist.DeletedById = deletedBy;
            watchlist.DeletedAt = deletedAt.UtcDateTime;

            await db.DbContext.SaveChangesAsync();
        }

        public async Task DeleteAdminMessage(int id, Guid deletedBy, DateTimeOffset deletedAt)
        {
            await using var db = await GetDb();

            var message = await db.DbContext.AdminMessages.Where(note => note.Id == id).SingleAsync();

            message.Deleted = true;
            message.DeletedById = deletedBy;
            message.DeletedAt = deletedAt.UtcDateTime;

            await db.DbContext.SaveChangesAsync();
        }

        public async Task HideBanFromNotes(int id, Guid deletedBy, DateTimeOffset deletedAt)
        {
            await using var db = await GetDb();

            var ban = await db.DbContext.Ban.Where(ban => ban.Id == id).SingleAsync();

            ban.Hidden = true;
            ban.LastEditedById = deletedBy;
            ban.LastEditedAt = deletedAt.UtcDateTime;

            await db.DbContext.SaveChangesAsync();
        }

        public async Task<List<IAdminRemarksRecord>> GetVisibleAdminRemarks(Guid player)
        {
            await using var db = await GetDb();
            List<IAdminRemarksRecord> notesCol = new();
            notesCol.AddRange(
                (await (from note in db.DbContext.AdminNotes
                        where note.PlayerUserId == player &&
                              !note.Secret &&
                              !note.Deleted &&
                              (note.ExpirationTime == null || DateTime.UtcNow < note.ExpirationTime)
                        select note)
                    .Include(note => note.Round)
                    .ThenInclude(r => r!.Server)
                    .Include(note => note.CreatedBy)
                    .Include(note => note.Player)
                    .ToListAsync()).Select(MakeAdminNoteRecord));
            notesCol.AddRange(await GetMessagesImpl(db, player));
            notesCol.AddRange(await GetBansAsNotesForUser(db, player));
            return notesCol;
        }

        public async Task<List<AdminWatchlistRecord>> GetActiveWatchlists(Guid player)
        {
            await using var db = await GetDb();
            return await GetActiveWatchlistsImpl(db, player);
        }

        protected async Task<List<AdminWatchlistRecord>> GetActiveWatchlistsImpl(DbGuard db, Guid player)
        {
            var entities = await (from watchlist in db.DbContext.AdminWatchlists
                          where watchlist.PlayerUserId == player &&
                                !watchlist.Deleted &&
                                (watchlist.ExpirationTime == null || DateTime.UtcNow < watchlist.ExpirationTime)
                          select watchlist)
                .Include(note => note.Round)
                .ThenInclude(r => r!.Server)
                .Include(note => note.CreatedBy)
                .Include(note => note.LastEditedBy)
                .Include(note => note.Player)
                .ToListAsync();

            return entities.Select(MakeAdminWatchlistRecord).ToList();
        }

        private AdminWatchlistRecord MakeAdminWatchlistRecord(AdminWatchlist entity)
        {
            return new AdminWatchlistRecord(entity.Id, MakeRoundRecord(entity.Round), MakePlayerRecord(entity.Player), entity.PlaytimeAtNote, entity.Message, MakePlayerRecord(entity.CreatedBy), NormalizeDatabaseTime(entity.CreatedAt), MakePlayerRecord(entity.LastEditedBy), NormalizeDatabaseTime(entity.LastEditedAt), NormalizeDatabaseTime(entity.ExpirationTime), entity.Deleted, MakePlayerRecord(entity.DeletedBy), NormalizeDatabaseTime(entity.DeletedAt));
        }

        public async Task<List<AdminMessageRecord>> GetMessages(Guid player)
        {
            await using var db = await GetDb();
            return await GetMessagesImpl(db, player);
        }

        protected async Task<List<AdminMessageRecord>> GetMessagesImpl(DbGuard db, Guid player)
        {
            var entities = await (from message in db.DbContext.AdminMessages
                        where message.PlayerUserId == player && !message.Deleted &&
                              (message.ExpirationTime == null || DateTime.UtcNow < message.ExpirationTime)
                        select message).Include(note => note.Round)
                    .ThenInclude(r => r!.Server)
                    .Include(note => note.CreatedBy)
                    .Include(note => note.LastEditedBy)
                    .Include(note => note.Player)
                    .ToListAsync();

            return entities.Select(MakeAdminMessageRecord).ToList();
        }

        public async Task MarkMessageAsSeen(int id, bool dismissedToo)
        {
            await using var db = await GetDb();
            var message = await db.DbContext.AdminMessages.SingleAsync(m => m.Id == id);
            message.Seen = true;
            if (dismissedToo)
                message.Dismissed = true;
            await db.DbContext.SaveChangesAsync();
        }

        private static IQueryable<Ban> BanRecordQuery(ServerDbContext dbContext)
        {
            return dbContext.Ban
                .Include(ban => ban.Unban)
                .Include(ban => ban.Rounds!)
                .ThenInclude(r => r.Round)
                .ThenInclude(r => r!.Server)
                .Include(ban => ban.Addresses)
                .Include(ban => ban.Players)
                .Include(ban => ban.Roles)
                .Include(ban => ban.Hwids)
                .Include(ban => ban.CreatedBy)
                .Include(ban => ban.LastEditedBy)
                .Include(ban => ban.Unban);
        }

        private async Task<BanNoteRecord> MakeBanNoteRecord(ServerDbContext dbContext, Ban ban)
        {
            var playerRecords = await AsyncSelect(ban.Players,
                async bp => MakePlayerRecord(bp.UserId,
                    await dbContext.Player.SingleOrDefaultAsync(p => p.UserId == bp.UserId)));

            return new BanNoteRecord(
                ban.Id,
                ban.Type,
                [..ban.Rounds!.Select(br => MakeRoundRecord(br.Round!))],
                [..playerRecords],
                ban.PlaytimeAtNote,
                ban.Reason,
                ban.Severity,
                MakePlayerRecord(ban.CreatedBy!),
                NormalizeDatabaseTime(ban.BanTime),
                MakePlayerRecord(ban.LastEditedBy!),
                NormalizeDatabaseTime(ban.LastEditedAt),
                NormalizeDatabaseTime(ban.ExpirationTime),
                ban.Hidden,
                ban.Unban?.UnbanningAdmin == null
                    ? null
                    : MakePlayerRecord(
                        ban.Unban.UnbanningAdmin.Value,
                        await dbContext.Player.SingleOrDefaultAsync(p => p.UserId == ban.Unban.UnbanningAdmin.Value)),
                NormalizeDatabaseTime(ban.Unban?.UnbanTime),
                [..ban.Roles!.Select(br => new BanRoleDef(br.RoleType, br.RoleId))]);
        }

        // These two are here because they get converted into notes later
        protected async Task<List<BanNoteRecord>> GetBansAsNotesForUser(DbGuard db, Guid user)
        {
            // You can't group queries, as player will not always exist. When it doesn't, the
            // whole query returns nothing
            var bans = await BanRecordQuery(db.DbContext)
                .AsSplitQuery()
                .Where(ban => ban.Players!.Any(bp => bp.UserId == user) && !ban.Hidden)
                .ToArrayAsync();

            var banNotes = new List<BanNoteRecord>();
            foreach (var ban in bans)
            {
                var banNote = await MakeBanNoteRecord(db.DbContext, ban);

                banNotes.Add(banNote);
            }

            return banNotes;
        }

        #endregion

        #region Job Whitelists

        public async Task<bool> AddJobWhitelist(Guid player, ProtoId<JobPrototype> job)
        {
            await using var db = await GetDb();
            var exists = await db.DbContext.RoleWhitelists
                .Where(w => w.PlayerUserId == player)
                .Where(w => w.RoleId == job.Id)
                .AnyAsync();

            if (exists)
                return false;

            var whitelist = new RoleWhitelist
            {
                PlayerUserId = player,
                RoleId = job
            };
            db.DbContext.RoleWhitelists.Add(whitelist);
            await db.DbContext.SaveChangesAsync();
            return true;
        }

        public async Task<List<string>> GetJobWhitelists(Guid player, CancellationToken cancel)
        {
            await using var db = await GetDb(cancel);
            return await db.DbContext.RoleWhitelists
                .Where(w => w.PlayerUserId == player)
                .Select(w => w.RoleId)
                .ToListAsync(cancellationToken: cancel);
        }

        public async Task<bool> IsJobWhitelisted(Guid player, ProtoId<JobPrototype> job)
        {
            await using var db = await GetDb();
            return await db.DbContext.RoleWhitelists
                .Where(w => w.PlayerUserId == player)
                .Where(w => w.RoleId == job.Id)
                .AnyAsync();
        }

        public async Task<bool> RemoveJobWhitelist(Guid player, ProtoId<JobPrototype> job)
        {
            await using var db = await GetDb();
            var entry = await db.DbContext.RoleWhitelists
                .Where(w => w.PlayerUserId == player)
                .Where(w => w.RoleId == job.Id)
                .SingleOrDefaultAsync();

            if (entry == null)
                return false;

            db.DbContext.RoleWhitelists.Remove(entry);
            await db.DbContext.SaveChangesAsync();
            return true;
        }

        #endregion

        # region IPIntel

        public async Task<bool> UpsertIPIntelCache(DateTime time, IPAddress ip, float score)
        {
            while (true)
            {
                try
                {
                    await using var db = await GetDb();

                    var existing = await db.DbContext.IPIntelCache
                        .Where(w => ip.Equals(w.Address))
                        .SingleOrDefaultAsync();

                    if (existing == null)
                    {
                        var newCache = new IPIntelCache
                        {
                            Time = time,
                            Address = ip,
                            Score = score,
                        };
                        db.DbContext.IPIntelCache.Add(newCache);
                    }
                    else
                    {
                        existing.Time = time;
                        existing.Score = score;
                    }

                    await Task.Delay(5000);

                    await db.DbContext.SaveChangesAsync();
                    return true;
                }
                catch (DbUpdateException)
                {
                    _opsLog.Warning("IPIntel UPSERT failed with a db exception... retrying.");
                }
            }
        }

        public async Task<IPIntelCache?> GetIPIntelCache(IPAddress ip)
        {
            await using var db = await GetDb();

            return await db.DbContext.IPIntelCache
                .SingleOrDefaultAsync(w => ip.Equals(w.Address));
        }

        public async Task<bool> CleanIPIntelCache(TimeSpan range)
        {
            await using var db = await GetDb();

            // Calculating this here cause otherwise sqlite whines.
            var cutoffTime = DateTime.UtcNow.Subtract(range);

            await db.DbContext.IPIntelCache
                .Where(w => w.Time <= cutoffTime)
                .ExecuteDeleteAsync();

            await db.DbContext.SaveChangesAsync();
            return true;
        }

        #endregion

        #region Custom vote logging

        public async Task<int> CustomVoteLogAdd(
            string title,
            int roundId,
            Guid? initiator,
            ImmutableArray<string> options)
        {
            await using var db = await GetDb();

            var log = new CustomVoteLog
            {
                Title = title,
                RoundId = roundId,
                InitiatorId = initiator,
                State = CustomVoteState.Active,
                TimeCreated = DateTime.UtcNow,
                Options = options.Select((o, i) => new CustomVoteLogOption
                    {
                        Text = o,
                        OptionIdx = (short)i,
                        VoteCount = 0,
                    })
                    .ToList(),
            };

            db.DbContext.CustomVoteLog.Add(log);
            await db.DbContext.SaveChangesAsync();

            return log.Id;
        }

        public async Task CustomVoteLogFinish(int voteId, ImmutableArray<int> voteCounts)
        {
            await using var db = await GetDb();

            var log = await db.DbContext.CustomVoteLog
                .Include(cvl => cvl.Options)
                .SingleAsync(v => v.Id == voteId);

            log.State = CustomVoteState.Finished;

            for (var i = 0; i < log.Options!.Count; i++)
            {
                log.Options[i].VoteCount = voteCounts[i];
            }

            await db.DbContext.SaveChangesAsync();
        }

        public async Task CustomVoteLogCancel(int voteId)
        {
            await using var db = await GetDb();

            var log = await db.DbContext.CustomVoteLog.SingleAsync(v => v.Id == voteId);
            log.State = CustomVoteState.Cancelled;

            await db.DbContext.SaveChangesAsync();
        }

        #endregion

        public abstract Task SendNotification(DatabaseNotification notification);

        // SQLite returns DateTime as Kind=Unspecified, Npgsql actually knows for sure it's Kind=Utc.
        // Normalize DateTimes here so they're always Utc. Thanks.
        protected abstract DateTime NormalizeDatabaseTime(DateTime time);

        [return: NotNullIfNotNull(nameof(time))]
        protected DateTime? NormalizeDatabaseTime(DateTime? time)
        {
            return time != null ? NormalizeDatabaseTime(time.Value) : time;
        }

        public async Task<bool> HasPendingModelChanges()
        {
            await using var db = await GetDb();
            return db.DbContext.Database.HasPendingModelChanges();
        }

        protected abstract Task<DbGuard> GetDb(
            CancellationToken cancel = default,
            [CallerMemberName] string? name = null);

        protected void LogDbOp(string? name)
        {
            _opsLog.Verbose($"Running DB operation: {name ?? "unknown"}");
        }

        protected abstract class DbGuard : IAsyncDisposable
        {
            public abstract ServerDbContext DbContext { get; }

            public abstract ValueTask DisposeAsync();
        }

        protected void NotificationReceived(DatabaseNotification notification)
        {
            OnNotificationReceived?.Invoke(notification);
        }

        public virtual void Shutdown()
        {

        }

        private static async Task<IEnumerable<TResult>> AsyncSelect<T, TResult>(
            IEnumerable<T>? enumerable,
            Func<T, Task<TResult>> selector)
        {
            var results = new List<TResult>();

            foreach (var item in enumerable ?? [])
            {
                results.Add(await selector(item));
            }

            return [..results];
        }

        private static async Task<List<T>> ParallelCollect<T>(params IEnumerable<Func<Task<IEnumerable<T>>>> tasks)
        {
            var taskInstances = tasks.Select(a => a());
            var results = await Task.WhenAll(taskInstances);
            return results.SelectMany(x => x).ToList();
        }
    }
}

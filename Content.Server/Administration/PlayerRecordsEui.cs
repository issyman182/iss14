using System.Globalization;
using System.Linq;
using Content.Server.Administration.Managers;
using Content.Server.Database;
using Content.Server.EUI;
using Content.Shared.Administration;
using Content.Shared.Eui;
using Robust.Shared.Network;

namespace Content.Server.Administration;

/// <summary>
/// Server side of the player records browser (<c>playerrecords</c>). Pages through every player who ever
/// connected (newest seen first), enriched with overall play time, ban count and migration status.
/// </summary>
public sealed partial class PlayerRecordsEui : BaseEui
{
#pragma warning disable IDE0044 // injected by [Dependency]
    [Dependency] private IAdminManager _admins = default!;
    [Dependency] private IServerDbManager _db = default!;
#pragma warning restore IDE0044

    private const int PageSize = 100;

    private string _filter;
    private int _page;
    private bool _hasMore;
    private List<PlayerRecordEntry> _rows = [];

    public PlayerRecordsEui(string filter)
    {
        IoCManager.InjectDependencies(this);
        _filter = filter;
    }

    public override void Opened()
    {
        base.Opened();
        _admins.OnPermsChanged += OnPermsChanged;
    }

    public override void Closed()
    {
        base.Closed();
        _admins.OnPermsChanged -= OnPermsChanged;
    }

    private void OnPermsChanged(AdminPermsChangedEventArgs args)
    {
        if (args.Player == Player)
            Load();
    }

    public override EuiStateBase GetNewState()
    {
        return new PlayerRecordsEuiState(_filter, _page, _hasMore, _rows);
    }

    private bool CanView()
    {
        return _admins.HasAdminFlag(Player, AdminFlags.Moderator);
    }

    public async void Load()
    {
        if (!CanView())
        {
            Close();
            return;
        }

        // Fetch one extra row to detect whether a further page exists.
        var info = await _db.GetPlayerRecordsInfoAsync(
            string.IsNullOrWhiteSpace(_filter) ? null : _filter,
            PageSize + 1,
            _page * PageSize);

        _hasMore = info.Count > PageSize;
        _rows = info.Take(PageSize).Select(Map).ToList();
        StateDirty();
    }

    private PlayerRecordEntry Map(PlayerRecordInfo info)
    {
        var r = info.Record;
        // iss14 fix: like upstream's player panel, connection identifiers (last seen IP) are only shown
        // to admins with the Ban flag, not to every Moderator.
        var canSeeAddress = _admins.HasAdminFlag(Player, AdminFlags.Ban);
        return new PlayerRecordEntry(
            r.UserId,
            r.LastSeenUserName,
            r.FirstSeenTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            r.LastSeenTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            canSeeAddress ? r.LastSeenAddress?.ToString() ?? "?" : "-",
            info.OverallPlaytime,
            info.BanCount,
            info.MigratedFrom,
            info.MigratedTo);
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);

        if (msg is not PlayerRecordsRequestMessage request)
            return;

        if (!CanView())
            return;

        _filter = request.Filter;
        _page = request.Page < 0 ? 0 : request.Page;
        Load();
    }
}

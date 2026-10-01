// iss14: GIFs in chat via GifSnap
using System.Globalization;
using Content.Server.Administration.Managers;
using Content.Server.EUI;
using Content.Shared.Administration;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.Eui;
using Content.Shared.Gifs;
using Content.Shared.Roles;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;

namespace Content.Server.Administration;

/// <summary>
/// Server side of the GIF config admin window (<c>gifconfig</c>). Reads/writes the <c>gifs.*</c> CVars live
/// (ARCHIVE CVars, same persistence path as the TTS panel) and edits the overall-playtime requirement of the
/// <c>gifs.role</c> gate job through <see cref="RoleRequirementOverrideManager"/>, exactly like the role editor.
/// </summary>
public sealed partial class GifConfigEui : BaseEui
{
    [Dependency] private IAdminManager _admins = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IPrototypeManager _proto = default!;
    [Dependency] private RoleRequirementOverrideManager _overrides = default!;

    private string _status = string.Empty;

    private GifConfigEuiState _state = new(false, false, 10f, 3, 5f, 6, 200, 150, 40, 1_200_000, true, true, false, 0f, "");

    public GifConfigEui()
    {
        IoCManager.InjectDependencies(this);
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
            BuildState();
    }

    private bool CanEdit() => _admins.HasAdminFlag(Player, AdminFlags.Server);

    public override EuiStateBase GetNewState() => _state;

    public void BuildState()
    {
        if (!CanEdit())
        {
            Close();
            return;
        }

        var allowed = GifConstants.ParseAllowedChannels(_cfg.GetCVar(CCVars.GifsAllowedChannels));
        var roleExists = TryGetRole(out var job);
        var hours = roleExists ? RequiredHours(job!) : 0f;

        _state = new GifConfigEuiState(
            true,
            _cfg.GetCVar(CCVars.GifsEnabled),
            _cfg.GetCVar(CCVars.GifsRateLimitPeriod),
            _cfg.GetCVar(CCVars.GifsRateLimitCount),
            _cfg.GetCVar(CCVars.GifsSearchRateLimitPeriod),
            _cfg.GetCVar(CCVars.GifsSearchRateLimitCount),
            _cfg.GetCVar(CCVars.GifsFrameWidth),
            _cfg.GetCVar(CCVars.GifsFrameHeight),
            _cfg.GetCVar(CCVars.GifsMaxFrames),
            _cfg.GetCVar(CCVars.GifsMaxSheetBytes),
            (allowed & ChatSelectChannel.OOC) != 0,
            (allowed & ChatSelectChannel.LOOC) != 0,
            roleExists,
            hours,
            _status);

        StateDirty();
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);

        if (!CanEdit())
            return;

        if (msg is GifConfigSetMessage m)
        {
            _status = string.Empty;
            ApplySet(m);
            BuildState();
        }
    }

    private void ApplySet(GifConfigSetMessage m)
    {
        switch (m.Field)
        {
            case GifConfigField.Enabled:
                _cfg.SetCVar(CCVars.GifsEnabled, bool.TryParse(m.Value, out var b) && b);
                break;
            case GifConfigField.SendPeriod:
                if (TryFloat(m.Value, out var sp))
                    _cfg.SetCVar(CCVars.GifsRateLimitPeriod, Math.Clamp(sp, 0.1f, 3600f));
                break;
            case GifConfigField.SendCount:
                if (TryInt(m.Value, out var sc))
                    _cfg.SetCVar(CCVars.GifsRateLimitCount, Math.Clamp(sc, 1, 1000));
                break;
            case GifConfigField.SearchPeriod:
                if (TryFloat(m.Value, out var qp))
                    _cfg.SetCVar(CCVars.GifsSearchRateLimitPeriod, Math.Clamp(qp, 0.1f, 3600f));
                break;
            case GifConfigField.SearchCount:
                if (TryInt(m.Value, out var qc))
                    _cfg.SetCVar(CCVars.GifsSearchRateLimitCount, Math.Clamp(qc, 1, 1000));
                break;
            case GifConfigField.FrameWidth:
                if (TryInt(m.Value, out var fw))
                    _cfg.SetCVar(CCVars.GifsFrameWidth, Math.Clamp(fw, 32, 1024));
                break;
            case GifConfigField.FrameHeight:
                if (TryInt(m.Value, out var fh))
                    _cfg.SetCVar(CCVars.GifsFrameHeight, Math.Clamp(fh, 32, 1024));
                break;
            case GifConfigField.MaxFrames:
                if (TryInt(m.Value, out var mf))
                    _cfg.SetCVar(CCVars.GifsMaxFrames, Math.Clamp(mf, 1, 400));
                break;
            case GifConfigField.MaxSheetBytes:
                if (TryInt(m.Value, out var mb))
                    _cfg.SetCVar(CCVars.GifsMaxSheetBytes, Math.Clamp(mb, 32 * 1024, 16_000_000));
                break;
            case GifConfigField.AllowOoc:
                SetChannel(ChatSelectChannel.OOC, bool.TryParse(m.Value, out var oo) && oo);
                break;
            case GifConfigField.AllowLooc:
                SetChannel(ChatSelectChannel.LOOC, bool.TryParse(m.Value, out var lo) && lo);
                break;
            case GifConfigField.RequiredHours:
                if (TryFloat(m.Value, out var hours))
                    SetRequiredHours(Math.Clamp(hours, 0f, 10_000f));
                break;
        }
    }

    private void SetChannel(ChatSelectChannel channel, bool allowed)
    {
        var mask = GifConstants.ParseAllowedChannels(_cfg.GetCVar(CCVars.GifsAllowedChannels));
        mask = allowed ? mask | channel : mask & ~channel;

        var parts = new List<string>();
        if ((mask & ChatSelectChannel.OOC) != 0)
            parts.Add("OOC");
        if ((mask & ChatSelectChannel.LOOC) != 0)
            parts.Add("LOOC");

        _cfg.SetCVar(CCVars.GifsAllowedChannels, string.Join(',', parts));
    }

    #region Playtime gate (via the role-requirement override manager)

    private bool TryGetRole(out JobPrototype? job)
    {
        job = null;
        var roleId = _cfg.GetCVar(CCVars.GifsRole);
        return !string.IsNullOrWhiteSpace(roleId) && _proto.TryIndex(roleId, out job);
    }

    /// <summary>Index of the first non-inverted overall-playtime requirement in the job's effective list, or -1.</summary>
    private int FindOverallRequirement(JobPrototype job)
    {
        var reqs = _overrides.GetEffectiveRequirements(job);
        for (var i = 0; i < reqs.Count; i++)
        {
            if (reqs[i] is OverallPlaytimeRequirement { Inverted: false })
                return i;
        }

        return -1;
    }

    private float RequiredHours(JobPrototype job)
    {
        var index = FindOverallRequirement(job);
        if (index < 0)
            return 0f;

        return (float) ((OverallPlaytimeRequirement) _overrides.GetEffectiveRequirements(job)[index]).Time.TotalHours;
    }

    private void SetRequiredHours(float hours)
    {
        if (!TryGetRole(out var job))
        {
            _status = Loc.GetString("gif-config-status-no-role", ("role", _cfg.GetCVar(CCVars.GifsRole)));
            return;
        }

        var index = FindOverallRequirement(job!);
        var time = TimeSpan.FromHours(hours);

        if (hours <= 0f)
        {
            if (index >= 0)
                _overrides.Remove(job!.ID, index);
        }
        else if (index >= 0)
        {
            _overrides.EditTime(job!.ID, index, time);
        }
        else
        {
            _overrides.Add(job!.ID, RoleReqKind.Overall, string.Empty, time, false);
        }

        _status = Loc.GetString("gif-config-status-role-saved", ("role", job!.ID));
    }

    #endregion

    private static bool TryInt(string value, out int result)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static bool TryFloat(string value, out float result)
        => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
}

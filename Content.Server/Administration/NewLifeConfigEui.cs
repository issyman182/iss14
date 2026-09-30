using System.Linq;
using Content.Server.Antag.Components;
using Content.Shared.Antag.Components; // iss14: AntagSelectionComponent moved to shared upstream
using Content.Server.Administration.Managers;
using Content.Server.EUI;
using Content.Server.NewLife;
using Content.Server.StationEvents.Components;
using Content.Shared.Administration;
using Content.Shared.Eui;
using Robust.Shared.Prototypes;

namespace Content.Server.Administration;

/// <summary>Server side of the New Life config admin window. Edits the <see cref="NewLifeManager"/>.</summary>
public sealed partial class NewLifeConfigEui : BaseEui
{
    [Dependency] private IAdminManager _admins = default!;
    [Dependency] private IPrototypeManager _proto = default!;
    [Dependency] private IComponentFactory _compFactory = default!;
    [Dependency] private IEntityManager _entMan = default!;
    [Dependency] private NewLifeManager _config = default!;

    public NewLifeConfigEui()
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

    private bool CanView() => _admins.HasAdminFlag(Player, AdminFlags.Server);
    private bool CanEdit() => _admins.HasAdminFlag(Player, AdminFlags.Server);

    public override EuiStateBase GetNewState() => _state;

    private NewLifeConfigEuiState _state =
        new(false, false, 300f, 3, false, new(), "", new(), false, "");

    public void BuildState()
    {
        if (!CanView())
        {
            Close();
            return;
        }

        // Offer round-defining rules: station events plus antag selection rules.
        var events = new List<NewLifeEventItem>();
        foreach (var proto in _proto.EnumeratePrototypes<EntityPrototype>())
        {
            if (proto.Abstract)
                continue;

            var isEvent = proto.TryGetComponent<StationEventComponent>(out _, _compFactory);
            var isAntag = proto.TryGetComponent<AntagSelectionComponent>(out _, _compFactory);
            if (!isEvent && !isAntag)
                continue;

            var display = string.IsNullOrWhiteSpace(proto.Name) ? proto.ID : proto.Name;
            events.Add(new NewLifeEventItem(proto.ID, display, _config.IsEventBlocked(proto.ID)));
        }
        events.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase));

        var blockedNow = _entMan.System<NewLifeSystem>().IsBlockedNow(out var reason);

        _state = new NewLifeConfigEuiState(
            CanEdit(),
            _config.Enabled,
            _config.Cooldown,
            _config.MaxLives,
            _config.KeepAntag,
            _config.ProfileNames.ToList(),
            _config.ActiveProfile,
            events,
            blockedNow,
            reason);

        StateDirty();
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);

        if (!CanEdit())
            return;

        switch (msg)
        {
            case NewLifeSetEnabledMessage m:
                _config.SetEnabled(m.Value);
                break;
            case NewLifeSetCooldownMessage m:
                _config.SetCooldown(m.Value);
                break;
            case NewLifeSetMaxMessage m:
                _config.SetMax(m.Value);
                break;
            case NewLifeSetKeepAntagMessage m:
                _config.SetKeepAntag(m.Value);
                break;
            case NewLifeSetActiveProfileMessage m:
                _config.SetActiveProfile(m.Profile);
                break;
            case NewLifeCreateProfileMessage m:
                _config.CreateProfile(m.Name);
                break;
            case NewLifeDeleteProfileMessage:
                _config.DeleteProfile();
                break;
            case NewLifeSetEventMessage m:
                _config.SetEvent(m.EventId, m.Blocked);
                break;
            default:
                return;
        }

        BuildState();
    }
}

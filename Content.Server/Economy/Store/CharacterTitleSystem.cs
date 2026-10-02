// iss14: Economy
using Content.Shared.Economy.Store;
using Content.Shared.GameTicking;
using Robust.Server.Player;
using Robust.Shared.Network;

namespace Content.Server.Economy.Store;

/// <summary>Puts the player's worn store title on their character and ID card at spawn (and on change).</summary>
public sealed partial class CharacterTitleSystem : EntitySystem
{
    [Dependency] private StoreManager _store = default!;
    [Dependency] private BankCardSystem _bankCard = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private Content.Server.Preferences.Managers.IServerPreferencesManager _prefs = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnSpawn);
    }

    private void OnSpawn(PlayerSpawnCompleteEvent ev)
    {
        Apply(ev.Mob, ev.Player.UserId, ev.Profile.Title);
    }

    /// <summary>Re-applies the title to whatever the player currently controls.</summary>
    public void Refresh(NetUserId userId)
    {
        if (!_players.TryGetSessionById(userId, out var session) || session.AttachedEntity is not { } mob)
            return;

        var profile = _prefs.GetPreferencesOrNull(userId)?.SelectedCharacter as Content.Shared.Preferences.HumanoidCharacterProfile;
        Apply(mob, userId, profile?.Title);
    }

    public void Apply(EntityUid mob, NetUserId userId, string? profileTitle)
    {
        var title = _store.ResolveTitle(userId, profileTitle);

        Set(mob, title);
        if (_bankCard.TryFindWornIdCard(mob, out var card))
            Set(card, title);
    }

    private void Set(EntityUid uid, string? title)
    {
        if (string.IsNullOrEmpty(title))
        {
            RemCompDeferred<CharacterTitleComponent>(uid);
            return;
        }

        var comp = EnsureComp<CharacterTitleComponent>(uid);
        comp.Title = title;
        Dirty(uid, comp);
    }
}

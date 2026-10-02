// iss14: Economy
using Content.Shared.Access.Components;
using Content.Shared.Economy;
using Content.Shared.Forensics.Components;
using Content.Shared.GameTicking;
using Content.Shared.Inventory;
using Content.Shared.PDA;
using Robust.Shared.Network;

namespace Content.Server.Economy;

/// <summary>
/// Binds the ID card a character spawns with to the player's bank account (<see cref="BankCardComponent"/>).
/// The account identity is the player, so every character of the same player shares one account; the card is
/// just the key. Fingerprint is recorded so ATMs can (optionally) verify the holder.
/// </summary>
public sealed partial class BankCardSystem : EntitySystem
{
    [Dependency] private InventorySystem _inventory = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
    }

    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent ev)
    {
        if (!TryFindWornIdCard(ev.Mob, out var card))
            return;

        Bind(card, ev.Player.UserId, ev.Mob);
    }

    /// <summary>The ID card in the mob's id slot, unwrapping a PDA if needed.</summary>
    public bool TryFindWornIdCard(EntityUid mob, out EntityUid card)
    {
        card = default;
        if (!_inventory.TryGetSlotEntity(mob, "id", out var idUid))
            return false;

        var cardId = idUid.Value;
        if (TryComp<PdaComponent>(idUid, out var pda) && pda.ContainedId != null)
            cardId = pda.ContainedId.Value;

        if (!HasComp<IdCardComponent>(cardId))
            return false;

        card = cardId;
        return true;
    }

    /// <summary>Stamps <paramref name="card"/> with the player's account and the mob's fingerprint/name.</summary>
    public void Bind(EntityUid card, NetUserId userId, EntityUid? mob)
    {
        var bank = EnsureComp<BankCardComponent>(card);
        bank.OwnerUserId = userId;
        bank.OwnerFingerprint = mob != null && TryComp<FingerprintComponent>(mob, out var fp) ? fp.Fingerprint : null;
        bank.OwnerName = mob != null ? Name(mob.Value) : null;
    }

    /// <summary>Whether the user's fingerprint matches the one stamped on the card.</summary>
    public AtmVerifyResult VerifyHolder(EntityUid card, EntityUid user)
    {
        if (!TryComp<BankCardComponent>(card, out var bank) || string.IsNullOrEmpty(bank.OwnerFingerprint))
            return AtmVerifyResult.Unknown;

        if (!TryComp<FingerprintComponent>(user, out var fp) || string.IsNullOrEmpty(fp.Fingerprint))
            return AtmVerifyResult.Unknown;

        return fp.Fingerprint == bank.OwnerFingerprint ? AtmVerifyResult.Match : AtmVerifyResult.Mismatch;
    }
}

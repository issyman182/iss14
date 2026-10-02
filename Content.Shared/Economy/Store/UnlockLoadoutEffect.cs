// iss14: Economy
using System.Diagnostics.CodeAnalysis;
using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Preferences.Loadouts.Effects;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Shared.Economy.Store;

/// <summary>
/// Loadout effect: the loadout is only available to players who bought the given store item.
/// </summary>
public sealed partial class UnlockLoadoutEffect : LoadoutEffect
{
    [DataField(required: true)]
    public ProtoId<StoreItemPrototype> Unlock;

    public override bool Validate(HumanoidCharacterProfile profile, RoleLoadout loadout, ICommonSession? session, IDependencyCollection collection, [NotNullWhen(false)] out FormattedMessage? reason)
    {
        if (session == null)
        {
            reason = FormattedMessage.Empty;
            return true;
        }

        var store = collection.Resolve<ISharedStoreManager>();
        if (store.HasUnlock(session, Unlock))
        {
            reason = null;
            return true;
        }

        var protoMan = collection.Resolve<IPrototypeManager>();
        var name = protoMan.TryIndex(Unlock, out var proto) ? Loc.GetString(proto.Name) : Unlock.Id;
        reason = FormattedMessage.FromMarkupPermissive(Loc.GetString("store-loadout-locked", ("item", name)));
        return false;
    }
}

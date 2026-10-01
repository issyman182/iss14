// iss14: EstacaoPirata playing cards (ported from Goob-Station)
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.Popups;
using Content.Server.Spawners.Components;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Item;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Server._EstacaoPirata.OpenTriggeredStorageFill;

/// <summary>
/// Fills a storage entity with its <see cref="OpenTriggeredStorageFillComponent"/> contents the first time it is opened,
/// so sealed deck boxes do not spawn their deck (and 53 cards) until someone actually opens them.
/// </summary>
public sealed partial class OpenTriggeredStorageFillSystem : EntitySystem
{
    [Dependency] private SharedStorageSystem _storage = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private IPrototypeManager _prototype = default!;
    [Dependency] private IComponentFactory _factory = default!;

    /// <inheritdoc/>
    public override void Initialize()
    {
        SubscribeLocalEvent<OpenTriggeredStorageFillComponent, ActivateInWorldEvent>(OnOpenEvent);
        SubscribeLocalEvent<OpenTriggeredStorageFillComponent, ExaminedEvent>(OnExamineEvent);
    }

    private void OnExamineEvent(EntityUid uid, OpenTriggeredStorageFillComponent component, ExaminedEvent args)
    {
        args.PushText(Loc.GetString("container-sealed"));
    }

    // Yes, that's a copy of StorageSystem StorageFill method
    private void OnOpenEvent(EntityUid uid, OpenTriggeredStorageFillComponent comp, ActivateInWorldEvent args)
    {
        var coordinates = Transform(uid).Coordinates;

        var spawnItems = EntitySpawnCollection.GetSpawns(comp.Contents);
        foreach (var item in spawnItems)
        {
            DebugTools.Assert(!_prototype.Index<EntityPrototype>(item)
                .HasComp<RandomSpawnerComponent>(_factory));
            var ent = Spawn(item, coordinates);

            if (!HasComp<ItemComponent>(ent))
            {
                Log.Error($"Tried to fill {ToPrettyString(uid)} with non-item {item}.");
                Del(ent);
                continue;
            }
            if (!_storage.Insert(uid, ent, out _, out var reason, playSound: false))
            {
                Log.Error($"Failed to fill {ToPrettyString(uid)} with {ToPrettyString(ent)}. Reason: {reason}");
                // Clean up the spawned entity if insertion fails
                Del(ent);
            }
        }
        _popup.PopupEntity(Loc.GetString("container-unsealed"), args.Target);
        RemComp(uid, comp);
    }
}

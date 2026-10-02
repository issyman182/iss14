// Ported from Starlight (https://github.com/ss14Starlight/space-station-14), station radio originally from Goob-Station.
// Starlight code is MIT / Starlight License; the Starlight License requires this attribution.
using Content.Shared._Starlight.StationRadio.Components;
using Content.Shared.Destructible;
using Content.Shared.Power;
using Robust.Shared.Containers;
using Content.Shared.Examine;

namespace Content.Shared._Starlight.StationRadio.Systems;

public abstract partial class SharedVinylPlayerSystem : EntitySystem
{
    [Dependency] private SharedContainerSystem _container = default!;

    // iss14: explicit subscriptions instead of Starlight's attribute-based subscriptions.
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<VinylPlayerComponent, PowerChangedEvent>(OnPowerChanged);
        SubscribeLocalEvent<VinylPlayerComponent, DestructionEventArgs>(OnDestruction);
        SubscribeLocalEvent<VinylPlayerComponent, EntInsertedIntoContainerMessage>(OnVinylInserted);
        SubscribeLocalEvent<VinylPlayerComponent, EntRemovedFromContainerMessage>(OnVinylRemove);
        SubscribeLocalEvent<VinylPlayerComponent, ExaminedEvent>(OnExamined);
    }

    protected virtual void OnPowerChanged(EntityUid uid, VinylPlayerComponent comp, PowerChangedEvent args)
    {
    }

    protected virtual void OnDestruction(EntityUid uid, VinylPlayerComponent comp, DestructionEventArgs args)
    {
    }

    protected virtual void OnVinylInserted(EntityUid uid, VinylPlayerComponent comp, EntInsertedIntoContainerMessage args)
    {
    }

    protected virtual void OnVinylRemove(EntityUid uid, VinylPlayerComponent comp, EntRemovedFromContainerMessage args)
    {
    }

    /// <summary>
    /// Show what vinyl is currently inserted when examined.
    /// </summary>
    private void OnExamined(EntityUid uid, VinylPlayerComponent comp, ref ExaminedEvent args)
    {
        if (!_container.TryGetContainer(uid, "vinyl", out var container) || container.ContainedEntities.Count == 0) // confirm actual container ID
        {
            args.PushMarkup(Loc.GetString("vinyl-player-examine-empty"));
            return;
        }

        var vinyl = container.ContainedEntities[0];
        args.PushMarkup(Loc.GetString("vinyl-player-examine-loaded", ("vinyl", Name(vinyl))));
    }
}

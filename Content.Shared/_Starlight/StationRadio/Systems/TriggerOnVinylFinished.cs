// Ported from Starlight (https://github.com/ss14Starlight/space-station-14), station radio originally from Goob-Station.
// Starlight code is MIT / Starlight License; the Starlight License requires this attribution.
using Content.Shared._Starlight.StationRadio.Components;
using Content.Shared._Starlight.StationRadio.Events;
using Content.Shared.Trigger;

namespace Content.Shared._Starlight.StationRadio.Systems;


public sealed partial class TriggerOnVinylFinishedSystem : TriggerOnXSystem
{
    public override void Initialize()
    {
        SubscribeLocalEvent<TriggerOnVinylFinishedComponent, VinylFinishedEvent>(OnVinylFinished);
    }

    private void OnVinylFinished(Entity<TriggerOnVinylFinishedComponent> ent, ref VinylFinishedEvent ev) => Trigger.Trigger(ent.Owner, ev.Player, ent.Comp.KeyOut);
}

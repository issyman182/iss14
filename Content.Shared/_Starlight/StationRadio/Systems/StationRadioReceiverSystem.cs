// Ported from Starlight (https://github.com/ss14Starlight/space-station-14), station radio originally from Goob-Station.
// Starlight code is MIT / Starlight License; the Starlight License requires this attribution.
using Content.Shared._Starlight.StationRadio.Components;
using Content.Shared._Starlight.StationRadio.Events;
using Content.Shared.Interaction;
using Content.Shared.Power;
using Content.Shared.Examine;
using Content.Shared.Verbs;

namespace Content.Shared._Starlight.StationRadio.Systems;

public abstract partial class SharedStationRadioReceiverSystem : EntitySystem
{
    // iss14: explicit subscriptions instead of Starlight's attribute-based subscriptions.
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<StationRadioReceiverComponent, ActivateInWorldEvent>(OnRadioToggle);
        SubscribeLocalEvent<StationRadioReceiverComponent, StationRadioMediaPlayedEvent>(OnMediaPlayed);
        SubscribeLocalEvent<StationRadioReceiverComponent, StationRadioMediaStoppedEvent>(OnMediaStopped);
        SubscribeLocalEvent<StationRadioReceiverComponent, MapInitEvent>(OnReceiverMapInit);
        SubscribeLocalEvent<StationRadioServerComponent, PowerChangedEvent>(OnServerPowerChanged);
        SubscribeLocalEvent<StationRadioReceiverComponent, GetVerbsEvent<AlternativeVerb>>(OnGetAltVerbs);
        SubscribeLocalEvent<StationRadioReceiverComponent, PowerChangedEvent>(OnPowerChanged);
        SubscribeLocalEvent<StationRadioServerComponent, EntityTerminatingEvent>(OnServerTerminating);
        SubscribeLocalEvent<RadioRigComponent, EntityTerminatingEvent>(OnRigTerminating);
        SubscribeLocalEvent<StationRadioServerComponent, AnchorStateChangedEvent>(OnAttemptAnchor);
        SubscribeLocalEvent<StationRadioReceiverComponent, ExaminedEvent>(OnExamined);
    }

    private void OnRadioToggle(EntityUid uid, StationRadioReceiverComponent comp, ActivateInWorldEvent args)
    {
        comp.Active = !comp.Active;
        Dirty(uid, comp);
    }

    protected virtual void OnMediaPlayed(EntityUid uid, StationRadioReceiverComponent comp, StationRadioMediaPlayedEvent args)
    {
    }

    protected virtual void OnMediaStopped(EntityUid uid, StationRadioReceiverComponent comp, StationRadioMediaStoppedEvent args)
    {
    }

    /// <summary>
    /// When a station radio is initialized, check any active Radio server for if there is an
    /// active song playing. If there is, attempt to resume play.
    /// </summary>
    protected virtual void OnReceiverMapInit(EntityUid uid, StationRadioReceiverComponent comp, MapInitEvent args)
    {
    }

    /// <summary>
    /// Stop broadcasting if the Radio Server loses power, despite the Vinyl Player and Rig still being powered.
    /// Resume play when the server power returns.
    /// </summary>
    protected virtual void OnServerPowerChanged(EntityUid uid, StationRadioServerComponent comp, PowerChangedEvent args)
    {
    }

    /// <summary>
    /// Method for getting the current volume of the station radio.
    /// </summary>
    protected static float GetGain(StationRadioReceiverComponent comp, bool powered)
        => comp.Active && powered ? 1f : 0f;

    /// <summary>
    /// Alt Click / Context Menu Verb for turning down the volume of the radio.
    /// </summary>
    private void OnGetAltVerbs(EntityUid uid, StationRadioReceiverComponent comp, GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract)
            return;

        args.Verbs.Add(new AlternativeVerb
        {
            Text = !comp.BoostVolume ? "Increase Volume" : "Decrease Volume",
            Act = () =>
            {
                comp.BoostVolume = !comp.BoostVolume;
                Dirty(uid, comp);
            }
        });
    }

    /// <summary>
    /// Stop broadcasting if the Radio Server loses power, despite the Vinyl Player and Rig still being powered.
    /// </summary>
    protected virtual void OnPowerChanged(EntityUid uid, StationRadioReceiverComponent comp, PowerChangedEvent args)
    {
    }

    /// <summary>
    /// When the Radio Server is destroyed, stop all station radio receivers.
    /// </summary>
    protected virtual void OnServerTerminating(EntityUid uid, StationRadioServerComponent comp, ref EntityTerminatingEvent args)
    {
    }

    /// <summary>
    /// When the Radio Rig is destroyed, stop all station radio receivers.
    /// </summary>
    protected virtual void OnRigTerminating(EntityUid uid, RadioRigComponent comp, ref EntityTerminatingEvent args)
    {
    }

    protected virtual void OnAttemptAnchor(EntityUid uid, StationRadioServerComponent comp, ref AnchorStateChangedEvent args)
    {
    }

    /// <summary>
    /// Display whether the station radio is at full or low volume when examined.
    /// </summary>
    private void OnExamined(EntityUid uid, StationRadioReceiverComponent comp, ref ExaminedEvent args) =>
        args.PushMarkup(Loc.GetString(!comp.BoostVolume
            ? "station-radio-receiver-examine-low-volume"
            : "station-radio-receiver-examine-full-volume"));

}

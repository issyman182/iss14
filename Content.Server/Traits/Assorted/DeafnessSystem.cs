using Content.Server.Radio;
using Content.Shared.Traits.Assorted;
using Content.Shared.Radio;

namespace Content.Server.Traits.Assorted;

/// <summary>
/// Prevents <see cref="DeafComponent"/> entities from receiving radio messages.
/// Local speech deafness is handled directly in <see cref="Content.Server.Chat.Systems.ChatSystem"/>.
/// </summary>
public sealed class DeafnessSystem : EntitySystem
{
    private EntityQuery<DeafComponent> _deafQuery;

    public override void Initialize()
    {
        base.Initialize();

        _deafQuery = GetEntityQuery<DeafComponent>();

        SubscribeLocalEvent<RadioReceiveAttemptEvent>(OnRadioReceiveAttempt);
    }

    private void OnRadioReceiveAttempt(ref RadioReceiveAttemptEvent args)
    {
        // The receiver is either the listener themselves (intrinsic radio) or a worn headset whose parent is the listener.
        var receiver = args.RadioReceiver;
        if (_deafQuery.HasComponent(receiver) || _deafQuery.HasComponent(Transform(receiver).ParentUid))
            args.Cancelled = true;
    }
}

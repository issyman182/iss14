using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared.Weapons.Ranged.Events;

[Serializable, NetSerializable]
public sealed partial class ClearJamDoAfterEvent : SimpleDoAfterEvent;

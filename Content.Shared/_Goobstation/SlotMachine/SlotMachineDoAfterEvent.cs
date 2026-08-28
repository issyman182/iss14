// SPDX-License-Identifier: AGPL-3.0-or-later
// Ported from Goobstation.

using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared._Goobstation.SlotMachine;

[Serializable, NetSerializable]
public sealed partial class SlotMachineDoAfterEvent : SimpleDoAfterEvent;

// iss14: EstacaoPirata playing cards (ported from Goob-Station)
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._EstacaoPirata.Cards.Hand;
using JetBrains.Annotations;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.UserInterface;

namespace Content.Client._EstacaoPirata.Cards.Hand.UI;

[UsedImplicitly]
public sealed partial class CardHandMenuBoundUserInterface : BoundUserInterface
{
    [Dependency] private IClyde _displayManager = default!;
    [Dependency] private IInputManager _inputManager = default!;

    private CardHandMenu? _menu;

    public CardHandMenuBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _menu = this.CreateWindow<CardHandMenu>();
        _menu.SetEntity(Owner);
        _menu.CardHandDrawMessageAction += SendCardHandDrawMessage;

        // Open the menu, centered on the mouse
        var vpSize = _displayManager.ScreenSize;
        _menu.OpenCenteredAt(_inputManager.MouseScreenPosition.Position / vpSize);
    }

    public void SendCardHandDrawMessage(NetEntity e)
    {
        SendMessage(new CardHandDrawMessage(e));
    }
}

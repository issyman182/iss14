// iss14
using Content.Client.Eui;
using Content.Shared.Administration;
using Content.Shared.Eui;
using JetBrains.Annotations;

namespace Content.Client.Administration.UI.EntityFinder;

[UsedImplicitly]
public sealed class EntityFinderEui : BaseEui
{
    private readonly EntityFinderWindow _window;

    public EntityFinderEui()
    {
        _window = new EntityFinderWindow();
        _window.OnSearch += (search, category, radius, sameMap) => SendMessage(new EntityFinderSearchMessage(search, category, radius, sameMap));
        _window.OnTeleport += entity => SendMessage(new EntityFinderTeleportMessage(entity));
        _window.OnClose += () => SendMessage(new CloseEuiMessage());
    }

    public override void HandleState(EuiStateBase state)
    {
        if (state is EntityFinderEuiState s)
            _window.SetState(s);
    }

    public override void Opened()
    {
        _window.OpenCentered();
    }

    public override void Closed()
    {
        _window.Close();
    }
}

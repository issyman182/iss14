using Content.Client.Eui;
using Content.Shared.Administration;
using Content.Shared.Eui;
using JetBrains.Annotations;

namespace Content.Client.Administration.UI.VoteConfig;

[UsedImplicitly]
public sealed class VoteConfigEui : BaseEui
{
    private readonly VoteConfigWindow _window;

    public VoteConfigEui()
    {
        _window = new VoteConfigWindow();

        _window.OnSetToggle += (toggle, value) => SendMessage(new VoteConfigSetToggleMessage(toggle, value));
        _window.OnSetMapPlayerCountFilter += value => SendMessage(new VoteConfigSetMapPlayerCountFilterMessage(value));
        _window.OnSetTimer += (timer, value) => SendMessage(new VoteConfigSetTimerMessage(timer, value));
        _window.OnSetActiveProfile += (isMap, profile) => SendMessage(new VoteConfigSetActiveProfileMessage(isMap, profile));
        _window.OnCreateProfile += (isMap, name) => SendMessage(new VoteConfigCreateProfileMessage(isMap, name));
        _window.OnDeleteProfile += isMap => SendMessage(new VoteConfigDeleteProfileMessage(isMap));
        _window.OnSetItem += (isMap, id, included) => SendMessage(new VoteConfigSetItemMessage(isMap, id, included));
        _window.OnSetFallback += (isMap, id) => SendMessage(new VoteConfigSetFallbackMessage(isMap, id));
        _window.OnSetMapRange += (mapId, min, max, clear) => SendMessage(new VoteConfigSetMapRangeMessage(mapId, min, max, clear));
        _window.OnClose += () => SendMessage(new CloseEuiMessage());
    }

    public override void HandleState(EuiStateBase state)
    {
        if (state is VoteConfigEuiState s)
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

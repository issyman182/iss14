using System.Globalization;
using System.Linq;
using Content.Server.Administration.Managers;
using Content.Server.EUI;
using Content.Server.TTS;
using Content.Shared.Administration;
using Content.Shared.CCVar;
using Content.Shared.Eui;
using Robust.Shared.Configuration;

namespace Content.Server.Administration;

/// <summary>
/// Server side of the TTS config admin window (<c>ttsconfig</c>). Reads/writes the <c>tts.*</c>
/// CVars live (which override <c>server_config.toml</c> and, being ARCHIVE, persist back to it),
/// manages the per-speaker gender assignments, and runs test synthesis through the <see cref="TTSSystem"/>.
/// </summary>
public sealed partial class TtsConfigEui : BaseEui
{
    [Dependency] private IAdminManager _admins = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IEntityManager _entMan = default!;

    private string _status = string.Empty;
    private bool _open;

    // iss14 fix: placeholder shown in the panel instead of the real tts.api_token.
    private const string TokenMask = "********";

    public TtsConfigEui()
    {
        IoCManager.InjectDependencies(this);
    }

    public override void Opened()
    {
        base.Opened();
        _open = true;
        _admins.OnPermsChanged += OnPermsChanged;

        // Auto-load the speaker list on first open so the dropdowns are populated.
        if (CanEdit()
            && Tts.Speakers.Count == 0
            && !string.IsNullOrWhiteSpace(_cfg.GetCVar(CCVars.TtsApiUrl)))
        {
            RefreshSpeakers();
        }
    }

    public override void Closed()
    {
        base.Closed();
        _open = false;
        _admins.OnPermsChanged -= OnPermsChanged;
    }

    private void OnPermsChanged(AdminPermsChangedEventArgs args)
    {
        if (args.Player == Player)
            BuildState();
    }

    private bool CanEdit() => _admins.HasAdminFlag(Player, AdminFlags.Server);

    private TTSSystem Tts => _entMan.System<TTSSystem>();

    public override EuiStateBase GetNewState() => _state;

    private TtsConfigEuiState _state = new(false, false, "", "", "", "", "af_heart", "kokoro", 1f, 400, 0.15f, new(), new(), "");

    public void BuildState()
    {
        if (!CanEdit())
        {
            Close();
            return;
        }

        var tts = Tts;
        var voices = tts.Speakers
            .Select(s => new TtsVoiceItem(s.Name, s.Language, tts.GetVoiceGender(s.Name)))
            .ToList();

        _state = new TtsConfigEuiState(
            true,
            _cfg.GetCVar(CCVars.TtsEnabled),
            _cfg.GetCVar(CCVars.TtsApiUrl),
            _cfg.GetCVar(CCVars.TtsSpeakersUrl),
            _cfg.GetCVar(CCVars.TtsModelsUrl),
            // iss14 fix: never ship the real bearer token to the client; show a mask when one is set.
            string.IsNullOrEmpty(_cfg.GetCVar(CCVars.TtsApiToken)) ? string.Empty : TokenMask,
            _cfg.GetCVar(CCVars.TtsDefaultSpeaker),
            _cfg.GetCVar(CCVars.TtsModel),
            _cfg.GetCVar(CCVars.TtsSpeed),
            _cfg.GetCVar(CCVars.TtsMaxMessageLength),
            _cfg.GetCVar(CCVars.TtsQueueDelay),
            tts.Models.ToList(),
            voices,
            _status);

        StateDirty();
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);

        if (!CanEdit())
            return;

        switch (msg)
        {
            case TtsConfigSetMessage m:
                ApplySet(m);
                BuildState();
                break;
            case TtsConfigSetVoiceGenderMessage m:
                Tts.SetVoiceGender(m.Speaker, m.Gender);
                BuildState();
                break;
            case TtsConfigSetAllGenderMessage m:
                Tts.SetAllVoiceGenders(m.Gender);
                BuildState();
                break;
            case TtsConfigAutoGenderMessage:
                Tts.AutoAssignGenders();
                BuildState();
                break;
            case TtsConfigRefreshSpeakersMessage:
                RefreshSpeakers();
                break;
            case TtsConfigTestMessage m:
                RunTest(m.Text, m.Speaker);
                break;
        }
    }

    private void ApplySet(TtsConfigSetMessage m)
    {
        switch (m.Field)
        {
            case TtsConfigField.Enabled:
                _cfg.SetCVar(CCVars.TtsEnabled, bool.TryParse(m.Value, out var b) && b);
                break;
            case TtsConfigField.ApiUrl:
                _cfg.SetCVar(CCVars.TtsApiUrl, m.Value.Trim());
                break;
            case TtsConfigField.SpeakersUrl:
                _cfg.SetCVar(CCVars.TtsSpeakersUrl, m.Value.Trim());
                break;
            case TtsConfigField.ModelsUrl:
                _cfg.SetCVar(CCVars.TtsModelsUrl, m.Value.Trim());
                break;
            case TtsConfigField.ApiToken:
                // iss14 fix: the client only ever sees the mask (or empty). Only apply a real new value so a
                // focus-exit on the untouched field doesn't overwrite the stored token with the mask.
                var newToken = m.Value.Trim();
                if (newToken.Length != 0 && newToken != TokenMask)
                    _cfg.SetCVar(CCVars.TtsApiToken, newToken);
                break;
            case TtsConfigField.DefaultSpeaker:
                _cfg.SetCVar(CCVars.TtsDefaultSpeaker, m.Value.Trim());
                break;
            case TtsConfigField.Model:
                _cfg.SetCVar(CCVars.TtsModel, m.Value.Trim());
                break;
            case TtsConfigField.Speed:
                if (float.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sp))
                    _cfg.SetCVar(CCVars.TtsSpeed, Math.Clamp(sp, 0.25f, 4f));
                break;
            case TtsConfigField.MaxLength:
                if (TryInt(m.Value, out var ml))
                    _cfg.SetCVar(CCVars.TtsMaxMessageLength, ml);
                break;
            case TtsConfigField.QueueDelay:
                if (float.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var qd))
                    _cfg.SetCVar(CCVars.TtsQueueDelay, Math.Clamp(qd, 0f, 10f));
                break;
        }
    }

    private void RefreshSpeakers()
    {
        _status = "Fetching speakers…";
        BuildState();

        Tts.RefreshSpeakers((ok, info) =>
        {
            _status = ok ? info : $"Speaker fetch failed: {info}";
            if (_open)
                BuildState();
        });
    }

    private void RunTest(string text, string speaker)
    {
        if (Player is not { } player)
            return;

        _status = "Synthesizing…";
        BuildState();

        Tts.QueueTest(player, text, speaker, (ok, info) =>
        {
            _status = ok ? "Test OK — playing audio." : $"Test failed: {info}";
            if (_open)
                BuildState();
        });
    }

    private static bool TryInt(string value, out int result)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
}

using System;
using System.Linq;
using System.Text;
using Content.Server.Speech.Prototypes;
using Content.Shared._EinsteinEngines.Language; // Einstein Engines - Language
using Content.Shared.Chat;
using Content.Shared.Eye.Blinding.Components; // Einstein Engines - Language (blindness check for visual languages)
using Content.Shared.Ghost;
using Content.Shared.Players;
using Robust.Shared.Console;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Utility;

namespace Content.Server.Chat.Systems;

public sealed partial class ChatSystem
{
    private enum MessageRangeCheckResult
    {
        Disallowed,
        HideChat,
        Full
    }

    /// <summary>
    ///     If hideChat should be set as far as replays are concerned.
    /// </summary>
    private bool MessageRangeHideChatForReplay(ChatTransmitRange range)
    {
        return range == ChatTransmitRange.HideChat;
    }

    /// <summary>
    ///     Checks if a target as returned from GetRecipients should receive the message.
    ///     Keep in mind data.Range is -1 for out of range observers.
    /// </summary>
    private MessageRangeCheckResult MessageRangeCheck(ICommonSession session, ICChatRecipientData data, ChatTransmitRange range)
    {
        var initialResult = MessageRangeCheckResult.Full;
        switch (range)
        {
            case ChatTransmitRange.Normal:
                initialResult = MessageRangeCheckResult.Full;
                break;
            case ChatTransmitRange.GhostRangeLimit:
                initialResult = (data.Observer && data.Range < 0 && !_adminManager.IsAdmin(session)) ? MessageRangeCheckResult.HideChat : MessageRangeCheckResult.Full;
                break;
            case ChatTransmitRange.HideChat:
                initialResult = MessageRangeCheckResult.HideChat;
                break;
            case ChatTransmitRange.NoGhosts:
                initialResult = (data.Observer && !_adminManager.IsAdmin(session)) ? MessageRangeCheckResult.Disallowed : MessageRangeCheckResult.Full;
                break;
        }
        var insistHideChat = data.HideChatOverride ?? false;
        var insistNoHideChat = !(data.HideChatOverride ?? true);
        if (insistHideChat && initialResult == MessageRangeCheckResult.Full)
            return MessageRangeCheckResult.HideChat;
        if (insistNoHideChat && initialResult == MessageRangeCheckResult.HideChat)
            return MessageRangeCheckResult.Full;
        return initialResult;
    }

    private enum SpeechHearing : byte
    {
        /// <summary>The listener hears the message clearly.</summary>
        Clear,
        /// <summary>The listener only catches a garbled version of the message.</summary>
        Muffled,
        /// <summary>The listener cannot hear the message at all.</summary>
        None,
    }

    /// <summary>
    ///     Determines how a listener perceives a spoken message based on their distance and any hearing-impairment traits.
    /// </summary>
    /// <param name="baseClearRange">
    ///     How close a normal (unimpaired) listener has to be to hear the message clearly.
    ///     Use <see cref="VoiceRange"/> for normal speech (everyone in range hears it) and <see cref="WhisperClearRange"/> for whispers.
    /// </param>
    private SpeechHearing GetSpeechHearing(EntityUid listener, float distance, bool observer, float baseClearRange)
    {
        // Ghosts and other observers always hear everything.
        if (observer)
            return SpeechHearing.Clear;

        // Fully deaf entities never hear local speech, including their own.
        if (_deafQuery.HasComponent(listener))
            return SpeechHearing.None;

        if (_hardOfHearingQuery.TryGetComponent(listener, out var hardOfHearing))
        {
            var clearRange = MathF.Min(baseClearRange, hardOfHearing.ClearRange);
            if (distance <= clearRange)
                return SpeechHearing.Clear;

            return distance <= hardOfHearing.MuffledRange ? SpeechHearing.Muffled : SpeechHearing.None;
        }

        return distance <= baseClearRange ? SpeechHearing.Clear : SpeechHearing.Muffled;
    }

    /// <summary>
    ///     Everything needed to rebuild a garbled variant of a spoken message for a hard-of-hearing listener.
    ///     LanguageObfuscatedMessage is the language-obfuscated variant for listeners who don't understand the language. (Einstein Engines - Language)
    /// </summary>
    private readonly record struct SpeechObfuscationData(string Message, string WrapId, string Name, string Verb, string FontId, int FontSize, string? LanguageObfuscatedMessage = null);

    /// <summary>
    ///     Sends a chat message to the given players in range of the source entity.
    /// </summary>
    /// <param name="obfuscation">
    ///     When set (only for the <see cref="ChatChannel.Local"/> say channel), hard-of-hearing listeners past their clear
    ///     range receive a garbled, freshly-rebuilt version of the message instead of the original.
    /// </param>
    private void SendInVoiceRange(ChatChannel channel, string message, string wrappedMessage, EntityUid source, ChatTransmitRange range, NetUserId? author = null, SpeechObfuscationData? obfuscation = null,
        LanguagePrototype? language = null, string? languageObfuscatedMessage = null, string? wrappedLanguageObfuscatedMessage = null) // Einstein Engines - Language
    {
        foreach (var (session, data) in GetRecipients(source, VoiceRange))
        {
            var entRange = MessageRangeCheck(session, data, range);
            if (entRange == MessageRangeCheckResult.Disallowed)
                continue;
            var entHideChat = entRange == MessageRangeCheckResult.HideChat;

            // Einstein Engines - Language: whether the listener understands the language spoken. Emotes/LOOC don't use languages.
            var canUnderstand = true;

            // Hearing impairment only affects spoken words, not emotes (seen) or LOOC (out-of-character).
            if (channel == ChatChannel.Local && session.AttachedEntity is { Valid: true } listener)
            {
                // Einstein Engines - Language begin
                if (language != null)
                    canUnderstand = _language.CanUnderstand(listener, language.ID);

                if (language != null && !language.SpeechOverride.RequireSpeech)
                {
                    // Visual languages (e.g. sign language) are seen, not heard: blocked by blindness rather than deafness.
                    if (!data.Observer && IsBlindListener(listener))
                        continue;
                }
                else
                // Einstein Engines - Language end
                switch (GetSpeechHearing(listener, data.Range, data.Observer, VoiceRange))
                {
                    case SpeechHearing.None:
                        continue;
                    case SpeechHearing.Muffled when obfuscation is { } obf && _hardOfHearingQuery.TryGetComponent(listener, out var hoh):
                        // Einstein Engines - Language: garble what the listener would actually perceive.
                        var perceived = canUnderstand ? obf.Message : obf.LanguageObfuscatedMessage ?? obf.Message;
                        var garbled = ObfuscateMessageReadability(perceived, hoh.Clarity);
                        var garbledWrap = Loc.GetString(obf.WrapId,
                            ("entityName", obf.Name),
                            ("verb", obf.Verb),
                            ("fontType", obf.FontId),
                            ("fontSize", obf.FontSize),
                            ("color", GetLanguageColor(language)), // Einstein Engines - Language
                            ("message", ApplyLanguageMarkup(language, FormattedMessage.EscapeText(garbled)))); // Einstein Engines - Language
                        _chatManager.ChatMessageToOne(channel, garbled, garbledWrap, source, entHideChat, session.Channel, author: author);
                        continue;
                }
            }

            // Einstein Engines - Language: listeners who don't understand the language get the obfuscated variant.
            if (!canUnderstand && languageObfuscatedMessage != null && wrappedLanguageObfuscatedMessage != null)
            {
                _chatManager.ChatMessageToOne(channel, languageObfuscatedMessage, wrappedLanguageObfuscatedMessage, source, entHideChat, session.Channel, author: author);
                continue;
            }

            _chatManager.ChatMessageToOne(channel, message, wrappedMessage, source, entHideChat, session.Channel, author: author);
        }

        _replay.RecordServerMessage(new ChatMessage(channel, message, wrappedMessage, GetNetEntity(source), null, MessageRangeHideChatForReplay(range)));
    }

    // Einstein Engines - Language begin
    /// <summary>
    ///     Whether the listener cannot see, and therefore cannot perceive visual languages such as sign language.
    /// </summary>
    private bool IsBlindListener(EntityUid listener)
    {
        return TryComp<BlindableComponent>(listener, out var blindable) && blindable.IsBlind;
    }

    /// <summary>
    ///     The effective chat color of the given language: its color override alpha-blended against <see cref="DefaultSpeakColor"/>.
    /// </summary>
    private Color GetLanguageColor(LanguagePrototype? language)
    {
        if (language?.SpeechOverride.Color is { } color && color.A > 0f)
            return Color.InterpolateBetween(DefaultSpeakColor, color, color.A);

        return DefaultSpeakColor;
    }

    /// <summary>
    ///     Decorates escaped message content with the language's color (and optionally font) markup.
    ///     Colors use alpha blending against <see cref="DefaultSpeakColor"/>, mirroring upstream Einstein Engines behavior.
    /// </summary>
    private string ApplyLanguageMarkup(LanguagePrototype? language, string escapedMessage, bool includeFont = false)
    {
        if (language == null)
            return escapedMessage;

        var result = escapedMessage;

        if (includeFont && language.SpeechOverride.FontId is { } fontId)
        {
            var size = language.SpeechOverride.FontSize is { } fontSize ? $" size={fontSize}" : string.Empty;
            result = $"[font=\"{fontId}\"{size}]{result}[/font]";
        }

        if (language.SpeechOverride.Color is { } color && color.A > 0f)
        {
            var blended = Color.InterpolateBetween(DefaultSpeakColor, color, color.A);
            result = $"[color={blended.ToHex()}]{result}[/color]";
        }

        return result;
    }
    // Einstein Engines - Language end

    /// <summary>
    ///     Returns true if the given player is 'allowed' to send the given message, false otherwise.
    /// </summary>
    private bool CanSendInGame(string message, IConsoleShell? shell = null, ICommonSession? player = null)
    {
        // Non-players don't have to worry about these restrictions.
        if (player == null)
            return true;

        var mindContainerComponent = player.ContentData()?.Mind;

        if (mindContainerComponent == null)
        {
            shell?.WriteError("You don't have a mind!");
            return false;
        }

        if (player.AttachedEntity is not { Valid: true } _)
        {
            shell?.WriteError("You don't have an entity!");
            return false;
        }

        return !_chatManager.MessageCharacterLimit(player, message);
    }

    // ReSharper disable once InconsistentNaming
    private string SanitizeInGameICMessage(EntityUid source, string message, out string? emoteStr, bool capitalize = true, bool punctuate = false, bool capitalizeTheWordI = true)
    {
        var newMessage = SanitizeMessageReplaceWords(message.Trim());

        GetRadioKeycodePrefix(source, newMessage, out newMessage, out var prefix);

        // Sanitize it first as it might change the word order
        _sanitizer.TrySanitizeEmoteShorthands(newMessage, source, out newMessage, out emoteStr);

        if (capitalize)
            newMessage = SanitizeMessageCapital(newMessage);
        if (capitalizeTheWordI)
            newMessage = SanitizeMessageCapitalizeTheWordI(newMessage, "i");
        if (punctuate)
            newMessage = SanitizeMessagePeriod(newMessage);

        return prefix + newMessage;
    }

    private string SanitizeInGameOOCMessage(string message)
    {
        var newMessage = message.Trim();
        newMessage = FormattedMessage.EscapeText(newMessage);

        return newMessage;
    }

    public string TransformSpeech(EntityUid sender, string message)
    {
        var ev = new TransformSpeechEvent(sender, message);
        RaiseLocalEvent(sender, ev, true);

        return ev.Message;
    }

    // Einstein Engines - Language: accents are not applied to languages that don't involve speech (e.g. sign language).
    public string TransformSpeech(EntityUid sender, string message, LanguagePrototype language)
    {
        return language.SpeechOverride.RequireSpeech ? TransformSpeech(sender, message) : message;
    }

    public bool CheckIgnoreSpeechBlocker(EntityUid sender, bool ignoreBlocker)
    {
        if (ignoreBlocker)
            return ignoreBlocker;

        var ev = new CheckIgnoreSpeechBlockerEvent(sender, ignoreBlocker);
        RaiseLocalEvent(sender, ev, true);

        return ev.IgnoreBlocker;
    }

    private IEnumerable<INetChannel> GetDeadChatClients()
    {
        return Filter.Empty()
            .AddWhereAttachedEntity(HasComp<GhostComponent>)
            .Recipients
            .Union(_adminManager.ActiveAdmins)
            .Select(p => p.Channel);
    }

    private string SanitizeMessagePeriod(string message)
    {
        if (string.IsNullOrEmpty(message))
            return message;
        // Adds a period if the last character is a letter.
        if (char.IsLetter(message[^1]))
            message += ".";
        return message;
    }

    public static readonly ProtoId<ReplacementAccentPrototype> ChatSanitize_Accent = "chatsanitize";

    public string SanitizeMessageReplaceWords(string message)
    {
        if (string.IsNullOrEmpty(message)) return message;

        var msg = message;

        msg = _wordreplacement.ApplyReplacements(msg, ChatSanitize_Accent);

        return msg;
    }

    /// <summary>
    ///     Returns list of players and ranges for all players withing some range. Also returns observers with a range of -1.
    /// </summary>
    private Dictionary<ICommonSession, ICChatRecipientData> GetRecipients(EntityUid source, float voiceGetRange)
    {
        // TODO proper speech occlusion

        var recipients = new Dictionary<ICommonSession, ICChatRecipientData>();

        var transformSource = Transform(source);
        var sourceMapId = transformSource.MapID;
        var sourceCoords = transformSource.Coordinates;

        foreach (var player in _playerManager.Sessions)
        {
            if (player.AttachedEntity is not { Valid: true } playerEntity)
                continue;

            var transformEntity = Transform(playerEntity);

            if (transformEntity.MapID != sourceMapId)
                continue;

            var observer = _ghostHearingQuery.HasComponent(playerEntity);

            // even if they are a ghost hearer, in some situations we still need the range
            if (sourceCoords.TryDistance(EntityManager, transformEntity.Coordinates, out var distance) && distance < voiceGetRange)
            {
                recipients.Add(player, new ICChatRecipientData(distance, observer));
                continue;
            }

            if (observer)
                recipients.Add(player, new ICChatRecipientData(-1, true));
        }

        RaiseLocalEvent(new ExpandICChatRecipientsEvent(source, voiceGetRange, recipients));
        return recipients;
    }

    public readonly record struct ICChatRecipientData(float Range, bool Observer, bool? HideChatOverride = null)
    {
    }

    private string ObfuscateMessageReadability(string message, float chance)
    {
        var modifiedMessage = new StringBuilder(message);

        for (var i = 0; i < message.Length; i++)
        {
            if (char.IsWhiteSpace((modifiedMessage[i])))
            {
                continue;
            }

            if (_random.Prob(1 - chance))
            {
                modifiedMessage[i] = '~';
            }
        }

        return modifiedMessage.ToString();
    }

    public string BuildGibberishString(IReadOnlyList<char> charOptions, int length)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < length; i++)
        {
            sb.Append(_random.Pick(charOptions));
        }
        return sb.ToString();
    }
}

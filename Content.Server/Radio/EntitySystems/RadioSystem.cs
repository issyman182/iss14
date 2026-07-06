using Content.Server.Administration.Logs;
using Content.Server.Chat.Managers;
using Content.Server.Chat.Systems;
using Content.Server._EinsteinEngines.Language; // Einstein Engines - Language
using Content.Shared._EinsteinEngines.Language; // Einstein Engines - Language
using Content.Server.Ghost;
using Content.Server.Power.Components;
using Content.Shared.Chat;
using Content.Shared.Database;
using Content.Shared.Mind;
using Content.Shared.Radio;
using Content.Shared.Radio.Components;
using Content.Shared.Roles.Jobs;
using Content.Shared.Speech;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Replays;
using Robust.Shared.Utility;

namespace Content.Server.Radio.EntitySystems;

/// <summary>
///     This system handles intrinsic radios and the general process of converting radio messages into chat messages.
/// </summary>
public sealed partial class RadioSystem : EntitySystem
{
    [Dependency] private INetManager _netMan = default!;
    [Dependency] private IReplayRecordingManager _replay = default!;
    [Dependency] private IAdminLogManager _adminLogger = default!;
    [Dependency] private IPrototypeManager _prototype = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private ChatSystem _chat = default!;
    [Dependency] private IChatManager _chatManager = default!;
    [Dependency] private GhostSystem _ghost = default!;
    [Dependency] private SharedMindSystem _mind = default!;
    [Dependency] private SharedJobSystem _jobs = default!;
    [Dependency] private EntityQuery<TelecomExemptComponent> _exemptQuery = default!;
    [Dependency] private LanguageSystem _language = default!; // Einstein Engines - Language

    // set used to prevent radio feedback loops.
    private readonly HashSet<string> _messages = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<IntrinsicRadioReceiverComponent, RadioReceiveEvent>(OnIntrinsicReceive);
        SubscribeLocalEvent<IntrinsicRadioTransmitterComponent, EntitySpokeEvent>(OnIntrinsicSpeak);
    }

    private void OnIntrinsicSpeak(EntityUid uid, IntrinsicRadioTransmitterComponent component, EntitySpokeEvent args)
    {
        if (args.Channel != null && component.Channels.Contains(args.Channel.ID))
        {
            SendRadioMessage(uid, args.Message, args.Channel, uid, language: args.Language); // Einstein Engines - Language
            args.Channel = null; // prevent duplicate messages from other listeners.
        }
    }

    private void OnIntrinsicReceive(EntityUid uid, IntrinsicRadioReceiverComponent component, ref RadioReceiveEvent args)
    {
        if (!TryComp(uid, out ActorComponent? actor))
            return;

        // Einstein Engines - Language: listeners who don't understand the language get the obfuscated variant.
        var baseMsg = _language.CanUnderstand(uid, args.Language.ID) ? args.ChatMsg : args.LanguageObfuscatedChatMsg;

        var msg = baseMsg;
        if (_ghost.CanGhostWarp(actor.PlayerSession, out _))
        {
            msg = new MsgChatMessage
            {
                Message = new ChatMessage(baseMsg.Message)
                {
                    WrappedMessage = _chatManager.PrependFollowButtonIfAppropriate(
                        baseMsg.Message.WrappedMessage,
                        args.MessageSource,
                        actor.PlayerSession.Channel),
                },
            };
        }

        _netMan.ServerSendMessage(msg, actor.PlayerSession.Channel);
    }

    /// <summary>
    /// Send radio message to all active radio listeners
    /// </summary>
    public void SendRadioMessage(EntityUid messageSource, string message, ProtoId<RadioChannelPrototype> channel, EntityUid radioSource, bool escapeMarkup = true, LanguagePrototype? language = null) // Einstein Engines - Language
    {
        SendRadioMessage(messageSource, message, _prototype.Index(channel), radioSource, escapeMarkup: escapeMarkup, language: language); // Einstein Engines - Language
    }

    /// <summary>
    /// Send radio message to all active radio listeners
    /// </summary>
    /// <param name="messageSource">Entity that spoke the message</param>
    /// <param name="radioSource">Entity that picked up the message and will send it, e.g. headset</param>
    public void SendRadioMessage(EntityUid messageSource, string message, RadioChannelPrototype channel, EntityUid radioSource, bool escapeMarkup = true, LanguagePrototype? language = null) // Einstein Engines - Language
    {
        // Einstein Engines - Language begin
        language ??= _language.GetLanguage(messageSource);

        // Languages that cannot be spoken over the radio are never transmitted.
        if (!language.SpeechOverride.AllowRadio)
            return;
        // Einstein Engines - Language end

        // TODO if radios ever garble / modify messages, feedback-prevention needs to be handled better than this.
        if (!_messages.Add(message))
            return;

        var evt = new TransformSpeakerNameEvent(messageSource, MetaData(messageSource).EntityName);
        RaiseLocalEvent(messageSource, evt);

        var name = evt.VoiceName;
        name = FormattedMessage.EscapeText(name);

        // Prepend the speaker's job icon before their name (skipped for antags / entities with no job role).
        if (_mind.TryGetMind(messageSource, out var mindId, out _)
            && _jobs.MindTryGetJobId(mindId, out var jobProtoId)
            && jobProtoId is { } jobId)
        {
            name = ChatIconTokens.JobIconMarkup(jobId.Id) + " " + name;
        }

        SpeechVerbPrototype speech;
        if (evt.SpeechVerb != null && _prototype.Resolve(evt.SpeechVerb, out var evntProto))
            speech = evntProto;
        else
            speech = _chat.GetSpeechVerb(messageSource, message);

        var content = escapeMarkup
            ? FormattedMessage.EscapeText(message)
            : message;

        // Einstein Engines - Language begin: language-aware wrapping (font/verb overrides + obfuscated variant).
        var verb = language.SpeechOverride.SpeechVerbOverrides is { } verbsOverride
            ? Loc.GetString(_random.Pick(verbsOverride).ToString())
            : Loc.GetString(_random.Pick(speech.SpeechVerbStrings));
        var fontType = language.SpeechOverride.FontId ?? speech.FontId;
        var fontSize = language.SpeechOverride.FontSize ?? speech.FontSize;
        var wrapId = speech.Bold ? "chat-radio-message-wrap-bold" : "chat-radio-message-wrap";

        var wrappedMessage = Loc.GetString(wrapId,
            ("color", channel.Color),
            ("fontType", fontType),
            ("fontSize", fontSize),
            ("verb", verb),
            ("channel", $"\\[{channel.LocalizedName}\\]"),
            ("name", name),
            ("message", ApplyLanguageColor(language, content)));

        // The variant for listeners who don't understand the language.
        var obfuscated = _language.ObfuscateSpeech(message, language);
        var obfuscatedContent = escapeMarkup ? FormattedMessage.EscapeText(obfuscated) : obfuscated;
        var wrappedObfuscated = Loc.GetString(wrapId,
            ("color", channel.Color),
            ("fontType", fontType),
            ("fontSize", fontSize),
            ("verb", verb),
            ("channel", $"\\[{channel.LocalizedName}\\]"),
            ("name", name),
            ("message", ApplyLanguageColor(language, obfuscatedContent)));
        // Einstein Engines - Language end

        // most radios are relayed to chat, so lets parse the chat message beforehand
        var chat = new ChatMessage(
            ChatChannel.Radio,
            message,
            wrappedMessage,
            NetEntity.Invalid,
            null);
        var chatMsg = new MsgChatMessage { Message = chat };

        // Einstein Engines - Language begin
        var obfuscatedChat = new ChatMessage(
            ChatChannel.Radio,
            obfuscated,
            wrappedObfuscated,
            NetEntity.Invalid,
            null);
        var obfuscatedChatMsg = new MsgChatMessage { Message = obfuscatedChat };

        var ev = new RadioReceiveEvent(message, messageSource, channel, radioSource, chatMsg, obfuscatedChatMsg, language);
        // Einstein Engines - Language end

        var sendAttemptEv = new RadioSendAttemptEvent(channel, radioSource);
        RaiseLocalEvent(ref sendAttemptEv);
        RaiseLocalEvent(radioSource, ref sendAttemptEv);
        var canSend = !sendAttemptEv.Cancelled;

        var sourceMapId = Transform(radioSource).MapID;
        var hasActiveServer = HasActiveServer(sourceMapId, channel.ID);
        var sourceServerExempt = _exemptQuery.HasComp(radioSource);

        var radioQuery = EntityQueryEnumerator<ActiveRadioComponent, TransformComponent>();
        while (canSend && radioQuery.MoveNext(out var receiver, out var radio, out var transform))
        {
            if (!radio.ReceiveAllChannels)
            {
                if (!radio.Channels.Contains(channel.ID) || (TryComp<IntercomComponent>(receiver, out var intercom) &&
                                                             !intercom.SupportedChannels.Contains(channel.ID)))
                    continue;
            }

            if (!channel.LongRange && transform.MapID != sourceMapId && !radio.GlobalReceive)
                continue;

            // don't need telecom server for long range channels or handheld radios and intercoms
            var needServer = !channel.LongRange && !sourceServerExempt;
            if (needServer && !hasActiveServer)
                continue;

            // check if message can be sent to specific receiver
            var attemptEv = new RadioReceiveAttemptEvent(channel, radioSource, receiver);
            RaiseLocalEvent(ref attemptEv);
            RaiseLocalEvent(receiver, ref attemptEv);
            if (attemptEv.Cancelled)
                continue;

            // send the message
            RaiseLocalEvent(receiver, ref ev);
        }

        if (name != Name(messageSource))
            _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Radio message from {ToPrettyString(messageSource):user} as {name} on {channel.LocalizedName}: {message}");
        else
            _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Radio message from {ToPrettyString(messageSource):user} on {channel.LocalizedName}: {message}");

        _replay.RecordServerMessage(chat);
        _messages.Remove(message);
    }

    // Einstein Engines - Language begin
    /// <summary>
    ///     Decorates radio message content with the language's color markup, if any.
    /// </summary>
    private static string ApplyLanguageColor(LanguagePrototype language, string content)
    {
        if (language.SpeechOverride.Color is not { } color || color.A <= 0f)
            return content;

        var blended = Color.InterpolateBetween(Color.White, color, color.A);
        return $"[color={blended.ToHex()}]{content}[/color]";
    }
    // Einstein Engines - Language end

    /// <inheritdoc cref="TelecomServerComponent"/>
    private bool HasActiveServer(MapId mapId, string channelId)
    {
        var servers = EntityQuery<TelecomServerComponent, EncryptionKeyHolderComponent, ApcPowerReceiverComponent, TransformComponent>();
        foreach (var (_, keys, power, transform) in servers)
        {
            if (transform.MapID == mapId &&
                power.Powered &&
                keys.Channels.Contains(channelId))
            {
                return true;
            }
        }
        return false;
    }
}

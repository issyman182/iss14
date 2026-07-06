using System;
using System.Linq;
using Content.Shared._EinsteinEngines.Language; // Einstein Engines - Language
using Content.Shared.Chat;
using Content.Shared.Database;
using Content.Shared.IdentityManagement;
using Content.Shared.Radio;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Robust.Shared.Utility;

namespace Content.Server.Chat.Systems;

public sealed partial class ChatSystem
{
    private void SendEntitySpeak(
        EntityUid source,
        string originalMessage,
        ChatTransmitRange range,
        string? nameOverride,
        LanguagePrototype language, // Einstein Engines - Language
        bool hideLog = false,
        bool ignoreActionBlocker = false
        )
    {
        if (!_actionBlocker.CanSpeak(source) && !ignoreActionBlocker)
            return;

        var message = TransformSpeech(source, originalMessage, language); // Einstein Engines - Language

        if (message.Length == 0)
            return;

        var speech = GetSpeechVerb(source, message);

        // get the entity's apparent name (if no override provided).
        string name;
        if (nameOverride != null)
        {
            name = nameOverride;
        }
        else
        {
            var nameEv = new TransformSpeakerNameEvent(source, Name(source));
            RaiseLocalEvent(source, nameEv);
            name = nameEv.VoiceName;
            // Check for a speech verb override
            if (nameEv.SpeechVerb != null && _prototypeManager.Resolve(nameEv.SpeechVerb, out var proto))
                speech = proto;
        }

        // Einstein Engines - Language begin
        if (!language.SpeechOverride.RequireSpeech)
        {
            // Since this is basically an emote (e.g. sign language), make it act like an emote for identity.
            var ent = Identity.Entity(source, EntityManager);
            name = nameOverride ?? Name(ent);
        }
        // Einstein Engines - Language end

        name = FormattedMessage.EscapeText(name);

        // Einstein Engines - Language begin: the language can override the speech verb, font and message wrap.
        var verb = language.SpeechOverride.SpeechVerbOverrides is { } verbsOverride
            ? Loc.GetString(_random.Pick(verbsOverride).ToString())
            : Loc.GetString(_random.Pick(speech.SpeechVerbStrings));
        var fontType = language.SpeechOverride.FontId ?? speech.FontId;
        var fontSize = language.SpeechOverride.FontSize ?? speech.FontSize;
        var wrapId = speech.Bold ? "chat-manager-entity-say-bold-wrap-message" : "chat-manager-entity-say-wrap-message";
        if (language.SpeechOverride.MessageWrapOverrides.TryGetValue(InGameICChatType.Speak, out var wrapOverride))
            wrapId = wrapOverride;

        var languageColor = GetLanguageColor(language); // custom language wraps (e.g. sign language) use a color argument
        var wrappedMessage = Loc.GetString(wrapId,
            ("entityName", name),
            ("verb", verb),
            ("fontType", fontType),
            ("fontSize", fontSize),
            ("color", languageColor),
            ("message", ApplyLanguageMarkup(language, FormattedMessage.EscapeText(message))));

        // The message as perceived by listeners who don't understand the language.
        var languageObfuscatedMessage = _language.ObfuscateSpeech(message, language);
        var wrappedLanguageObfuscatedMessage = Loc.GetString(wrapId,
            ("entityName", name),
            ("verb", verb),
            ("fontType", fontType),
            ("fontSize", fontSize),
            ("color", languageColor),
            ("message", ApplyLanguageMarkup(language, FormattedMessage.EscapeText(languageObfuscatedMessage))));
        // Einstein Engines - Language end

        // Pass the raw pieces so hard-of-hearing listeners past their clear range can be given a garbled variant.
        var obfuscation = new SpeechObfuscationData(message, wrapId, name, verb, fontType, fontSize, languageObfuscatedMessage); // Einstein Engines - Language

        SendInVoiceRange(ChatChannel.Local, message, wrappedMessage, source, range, obfuscation: obfuscation,
            language: language, languageObfuscatedMessage: languageObfuscatedMessage, wrappedLanguageObfuscatedMessage: wrappedLanguageObfuscatedMessage); // Einstein Engines - Language

        var ev = new EntitySpokeEvent(source, message, null, null, language); // Einstein Engines - Language
        RaiseLocalEvent(source, ev, true);

        // To avoid logging any messages sent by entities that are not players, like vendors, cloning, etc.
        // Also doesn't log if hideLog is true.
        if (!HasComp<ActorComponent>(source) || hideLog)
            return;

        if (originalMessage == message)
        {
            if (name != Name(source))
                _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Say from {source} as {name}: {originalMessage}.");
            else
                _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Say from {source}: {originalMessage}.");
        }
        else
        {
            if (name != Name(source))
                _adminLogger.Add(LogType.Chat, LogImpact.Low,
                    $"Say from {source} as {name}, original: {originalMessage}, transformed: {message}.");
            else
                _adminLogger.Add(LogType.Chat, LogImpact.Low,
                    $"Say from {source}, original: {originalMessage}, transformed: {message}.");
        }
    }

    private void SendEntityWhisper(
        EntityUid source,
        string originalMessage,
        ChatTransmitRange range,
        RadioChannelPrototype? channel,
        string? nameOverride,
        LanguagePrototype language, // Einstein Engines - Language
        bool hideLog = false,
        bool ignoreActionBlocker = false
        )
    {
        if (!_actionBlocker.CanSpeak(source) && !ignoreActionBlocker)
            return;

        // Einstein Engines - Language: languages that don't work over the radio never reach the channel.
        if (channel != null && !language.SpeechOverride.AllowRadio)
            channel = null;

        var message = TransformSpeech(source, FormattedMessage.RemoveMarkupOrThrow(originalMessage), language); // Einstein Engines - Language
        if (message.Length == 0)
            return;

        var obfuscatedMessage = ObfuscateMessageReadability(message, 0.2f);

        // Einstein Engines - Language begin: the variants of the message for listeners who don't understand the language.
        var languageObfuscatedMessage = _language.ObfuscateSpeech(message, language);
        var languageObfuscatedGarbledMessage = ObfuscateMessageReadability(languageObfuscatedMessage, 0.2f);
        // Einstein Engines - Language end

        // get the entity's name by visual identity (if no override provided).
        string nameIdentity = FormattedMessage.EscapeText(nameOverride ?? Identity.Name(source, EntityManager));
        // get the entity's name by voice (if no override provided).
        string name;
        if (nameOverride != null)
        {
            name = nameOverride;
        }
        else
        {
            var nameEv = new TransformSpeakerNameEvent(source, Name(source));
            RaiseLocalEvent(source, nameEv);
            name = nameEv.VoiceName;
        }

        // Einstein Engines - Language begin
        if (!language.SpeechOverride.RequireSpeech)
        {
            // Since this is basically an emote (e.g. sign language), make it act like an emote for identity.
            var ent = Identity.Entity(source, EntityManager);
            name = nameOverride ?? Name(ent);
        }
        // Einstein Engines - Language end

        name = FormattedMessage.EscapeText(name);

        // Einstein Engines - Language begin: language-aware wrapping. The language may override the whisper wrap.
        var wrapId = "chat-manager-entity-whisper-wrap-message";
        var unknownWrapId = "chat-manager-entity-whisper-unknown-wrap-message";
        if (language.SpeechOverride.MessageWrapOverrides.TryGetValue(InGameICChatType.Whisper, out var wrapOverride))
            wrapId = unknownWrapId = wrapOverride;

        // Custom language wraps (e.g. sign language) also use verb/font/color arguments.
        var whisperSpeech = GetSpeechVerb(source, message);
        var whisperVerb = language.SpeechOverride.SpeechVerbOverrides is { } whisperVerbsOverride
            ? Loc.GetString(_random.Pick(whisperVerbsOverride).ToString())
            : Loc.GetString(_random.Pick(whisperSpeech.SpeechVerbStrings));
        var whisperColor = GetLanguageColor(language);

        string WrapWhisper(string locId, string entityName, string msg)
        {
            return Loc.GetString(locId,
                ("entityName", entityName),
                ("verb", whisperVerb),
                ("fontType", language.SpeechOverride.FontId ?? whisperSpeech.FontId),
                ("fontSize", language.SpeechOverride.FontSize ?? whisperSpeech.FontSize),
                ("color", whisperColor),
                ("message", ApplyLanguageMarkup(language, FormattedMessage.EscapeText(msg), includeFont: true)));
        }

        var wrappedMessage = WrapWhisper(wrapId, name, message);
        var wrappedobfuscatedMessage = WrapWhisper(wrapId, nameIdentity, obfuscatedMessage);
        var wrappedUnknownMessage = WrapWhisper(unknownWrapId, string.Empty, obfuscatedMessage);

        // Same three wraps, but for listeners who don't understand the spoken language.
        var wrappedLanguageMessage = WrapWhisper(wrapId, name, languageObfuscatedMessage);
        var wrappedLanguageObfuscatedMessage = WrapWhisper(wrapId, nameIdentity, languageObfuscatedGarbledMessage);
        var wrappedLanguageUnknownMessage = WrapWhisper(unknownWrapId, string.Empty, languageObfuscatedGarbledMessage);
        // Einstein Engines - Language end

        foreach (var (session, data) in GetRecipients(source, WhisperMuffledRange))
        {
            EntityUid listener;

            if (session.AttachedEntity is not { Valid: true } playerEntity)
                continue;
            listener = session.AttachedEntity.Value;

            if (MessageRangeCheck(session, data, range) != MessageRangeCheckResult.Full)
                continue; // Won't get logged to chat, and ghosts are too far away to see the pop-up, so we just won't send it to them.

            // Hearing impairment: the deaf hear nothing, the hard-of-hearing have a smaller clear range and can miss a whisper entirely.
            float whisperClearRange = WhisperClearRange;
            if (!data.Observer)
            {
                // Einstein Engines - Language: visual languages (e.g. sign language) are seen rather than heard.
                if (!language.SpeechOverride.RequireSpeech)
                {
                    if (IsBlindListener(listener))
                        continue;
                }
                else
                {
                    if (_deafQuery.HasComponent(listener))
                        continue;

                    if (_hardOfHearingQuery.TryGetComponent(listener, out var hardOfHearing))
                    {
                        if (data.Range > hardOfHearing.MuffledRange)
                            continue;
                        whisperClearRange = MathF.Min(whisperClearRange, hardOfHearing.ClearRange);
                    }
                }
            }

            // Einstein Engines - Language: what the listener perceives depends on whether they understand the language.
            var canUnderstand = _language.CanUnderstand(listener, language.ID);

            if (data.Range <= whisperClearRange || data.Observer)
                _chatManager.ChatMessageToOne(ChatChannel.Whisper,
                    canUnderstand ? message : languageObfuscatedMessage,
                    canUnderstand ? wrappedMessage : wrappedLanguageMessage,
                    source, false, session.Channel); // Einstein Engines - Language
            //If listener is too far, they only hear fragments of the message
            else if (_examineSystem.InRangeUnOccluded(source, listener, WhisperMuffledRange))
                _chatManager.ChatMessageToOne(ChatChannel.Whisper,
                    canUnderstand ? obfuscatedMessage : languageObfuscatedGarbledMessage,
                    canUnderstand ? wrappedobfuscatedMessage : wrappedLanguageObfuscatedMessage,
                    source, false, session.Channel); // Einstein Engines - Language
            //If listener is too far and has no line of sight, they can't identify the whisperer's identity
            else
                _chatManager.ChatMessageToOne(ChatChannel.Whisper,
                    canUnderstand ? obfuscatedMessage : languageObfuscatedGarbledMessage,
                    canUnderstand ? wrappedUnknownMessage : wrappedLanguageUnknownMessage,
                    source, false, session.Channel); // Einstein Engines - Language
        }

        _replay.RecordServerMessage(new ChatMessage(ChatChannel.Whisper, message, wrappedMessage, GetNetEntity(source), null, MessageRangeHideChatForReplay(range)));

        var ev = new EntitySpokeEvent(source, message, channel, obfuscatedMessage, language); // Einstein Engines - Language
        RaiseLocalEvent(source, ev, true);
        if (!hideLog)
            if (originalMessage == message)
            {
                if (name != Name(source))
                    _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Whisper from {source} as {name}: {originalMessage}.");
                else
                    _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Whisper from {source}: {originalMessage}.");
            }
            else
            {
                if (name != Name(source))
                    _adminLogger.Add(LogType.Chat, LogImpact.Low,
                    $"Whisper from {source} as {name}, original: {originalMessage}, transformed: {message}.");
                else
                    _adminLogger.Add(LogType.Chat, LogImpact.Low,
                    $"Whisper from {source}, original: {originalMessage}, transformed: {message}.");
            }
    }

    protected override void SendEntityEmote(
        EntityUid source,
        string action,
        ChatTransmitRange range,
        string? nameOverride,
        bool hideLog = false,
        bool checkEmote = true,
        bool ignoreActionBlocker = false,
        NetUserId? author = null
        )
    {
        if (!_actionBlocker.CanEmote(source) && !ignoreActionBlocker)
            return;

        // get the entity's apparent name (if no override provided).
        var ent = Identity.Entity(source, EntityManager);
        string name = FormattedMessage.EscapeText(nameOverride ?? Name(ent));

        // Emotes use Identity.Name, since it doesn't actually involve your voice at all.
        var wrappedMessage = Loc.GetString("chat-manager-entity-me-wrap-message",
            ("entityName", name),
            ("entity", ent),
            ("message", FormattedMessage.RemoveMarkupOrThrow(action)));

        if (checkEmote &&
            !TryEmoteChatInput(source, action))
            return;

        SendInVoiceRange(ChatChannel.Emotes, action, wrappedMessage, source, range, author);
        if (!hideLog)
            if (name != Name(source))
                _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Emote from {source} as {name}: {action}");
            else
                _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Emote from {source}: {action}");
    }

    // ReSharper disable once InconsistentNaming
    private void SendLOOC(EntityUid source, ICommonSession player, string message, bool hideChat)
    {
        var name = FormattedMessage.EscapeText(Identity.Name(source, EntityManager));

        if (_adminManager.IsAdmin(player))
        {
            if (!_adminLoocEnabled) return;
        }
        else if (!_loocEnabled) return;

        // If crit player LOOC is disabled, don't send the message at all.
        if (!_critLoocEnabled && _mobStateSystem.IsCritical(source))
            return;

        var wrappedMessage = Loc.GetString("chat-manager-entity-looc-wrap-message",
            ("entityName", name),
            ("message", FormattedMessage.EscapeText(message)));

        SendInVoiceRange(ChatChannel.LOOC, message, wrappedMessage, source, hideChat ? ChatTransmitRange.HideChat : ChatTransmitRange.Normal, player.UserId);
        _adminLogger.Add(LogType.Chat, LogImpact.Low, $"LOOC from {source}: {message}");
    }

    private void SendDeadChat(EntityUid source, ICommonSession player, string message, bool hideChat)
    {
        var clients = GetDeadChatClients();
        var playerName = Name(source);
        string wrappedMessage;
        if (_adminManager.IsAdmin(player))
        {
            wrappedMessage = Loc.GetString("chat-manager-send-admin-dead-chat-wrap-message",
                ("adminChannelName", Loc.GetString("chat-manager-admin-channel-name")),
                ("userName", player.Channel.UserName),
                ("message", FormattedMessage.EscapeText(message)));
            _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Admin dead chat from {source}: {message}");
        }
        else
        {
            wrappedMessage = Loc.GetString("chat-manager-send-dead-chat-wrap-message",
                ("deadChannelName", Loc.GetString("chat-manager-dead-channel-name")),
                ("playerName", (playerName)),
                ("message", FormattedMessage.EscapeText(message)));
            _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Dead chat from {source}: {message}");
        }

        _chatManager.ChatMessageToMany(ChatChannel.Dead, message, wrappedMessage, source, hideChat, true, clients.ToList(), author: player.UserId);
    }
}

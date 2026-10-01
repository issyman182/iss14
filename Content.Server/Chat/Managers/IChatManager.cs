using System.Diagnostics.CodeAnalysis;
using Content.Shared.Administration;
using Content.Shared.Chat;
using Content.Shared.Players.RateLimiting;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server.Chat.Managers
{
    public interface IChatManager : ISharedChatManager
    {
        /// <summary>
        ///     Dispatch a server announcement to every connected player.
        /// </summary>
        /// <param name="message"></param>
        /// <param name="colorOverride">Override the color of the message being sent.</param>
        /// <param name="sender">Session of the administrator who initiated the announcement.</param>
        void DispatchServerAnnouncement(string message, Color? colorOverride = null, ICommonSession? sender = null);

        void DispatchServerMessage(ICommonSession player, string message, bool suppressLog = false);

        /// <param name="wrappedMarkupSuffix">
        ///     iss14: optional trusted markup appended to the wrapped (displayed) message only, e.g. an inline GIF tag.
        ///     Never pass client-controlled text here.
        /// </param>
        /// <returns>iss14: true if the message was actually broadcast.</returns>
        bool TrySendOOCMessage(ICommonSession player, string message, OOCChatType type, string? wrappedMarkupSuffix = null);

        void SendHookOOC(string sender, string message);
        void SendHookAdmin(string sender, string message);
        void SendAdminAnnouncement(string message, AdminFlags? flagBlacklist = null, AdminFlags? flagWhitelist = null);
        void SendAdminAnnouncementMessage(ICommonSession player, string message, bool suppressLog = true);

        void ChatMessageToOne(ChatChannel channel, string message, string wrappedMessage, EntityUid source, bool hideChat,
            INetChannel client, Color? colorOverride = null, bool recordReplay = false, string? audioPath = null, float audioVolume = 0, NetUserId? author = null);

        void ChatMessageToMany(ChatChannel channel, string message, string wrappedMessage, EntityUid source, bool hideChat, bool recordReplay,
            IEnumerable<INetChannel> clients, Color? colorOverride = null, string? audioPath = null, float audioVolume = 0, NetUserId? author = null);

        void ChatMessageToManyFiltered(Filter filter, ChatChannel channel, string message, string wrappedMessage, EntityUid source, bool hideChat, bool recordReplay, Color? colorOverride, string? audioPath = null, float audioVolume = 0);

        void ChatMessageToAll(ChatChannel channel, string message, string wrappedMessage, EntityUid source, bool hideChat, bool recordReplay, Color? colorOverride = null, string? audioPath = null, float audioVolume = 0, NetUserId? author = null);

        bool MessageCharacterLimit(ICommonSession player, string message);

        void DeleteMessagesBy(NetUserId uid);

        [return: NotNullIfNotNull(nameof(author))]
        ChatUser? EnsurePlayer(NetUserId? author);

        /// <summary>
        /// Called when a player sends a chat message to handle rate limits.
        /// Will update counts and do necessary actions if breached.
        /// </summary>
        /// <param name="player">The player sending a chat message.</param>
        /// <returns>False if the player has violated rate limits and should be blocked from sending further messages.</returns>
        RateLimitStatus HandleRateLimit(ICommonSession player);

        /// <summary>
        /// iss14: true if the player is under an admin chat timeout (see <c>timeout</c> command). When so, the
        /// player is told how long is left; callers must drop the message.
        /// </summary>
        bool IsChatTimedOut(ICommonSession player);
    }
}

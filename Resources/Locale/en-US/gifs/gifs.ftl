# iss14: GIFs in chat via GifSnap

## Chat box / picker
gifs-chat-button = GIF
gifs-chat-button-tooltip = Send a GIF in OOC/LOOC chat
gifs-window-title = Send a GIF
gifs-search-placeholder = Search GIFs… (empty for trending)
gifs-search-button = Search
gifs-prev-page = ◀
gifs-next-page = ▶
gifs-page = Page { $page }
gifs-attribution = Powered by GifSnap
gifs-channel-note = Posting to { $channel }
gifs-channel-fallback = Your current channel can't take GIFs; posting to { $channel } instead
gifs-status-searching = Searching…
gifs-status-trending = Trending GIFs. Click one to send it.
gifs-status-results = { $count } results. Click one to send it.
gifs-status-no-results = No GIFs found.
gifs-status-sent = Sending…
gifs-status-requirements = You can't send GIFs yet: { $reason }

## Chat line
# Plain-text version of a GIF chat line (logs, replays, Discord relay). The animated GIF is shown after it.
gifs-chat-message = [GIF] { $title }
gifs-untitled = untitled
gifs-loading = GIF: { $title }

## Errors (sent by the server as keys)
gifs-error-disabled = GIFs are disabled on this server.
gifs-error-invalid = That GIF id isn't valid.
gifs-error-channel = GIFs can only be sent to OOC or LOOC.
gifs-error-no-entity = You need a body to send a GIF in LOOC.
gifs-error-not-allowed = You don't meet the playtime requirement to send GIFs yet.
gifs-error-rate-limited = You're sending GIF requests too quickly.
gifs-error-busy = The server is busy with other GIFs; try again in a moment.
gifs-error-fetch-failed = Couldn't reach the GIF service.
gifs-error-not-found = That GIF doesn't exist (anymore).
gifs-error-too-large = That GIF is too large to send.
gifs-error-chat-rejected = Your GIF wasn't sent (chat is disabled or you can't speak there right now).

## Hidden pseudo-job used to gate GIF sending
job-name-gif-sender = GIF sender
job-description-gif-sender = Not a real job: its requirements decide who may send GIFs in OOC/LOOC chat.

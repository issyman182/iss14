# iss14: chat timeouts / GIF mutes
chat-timeout-notice = You are timed out from chat for { $minutes } more { $minutes ->
    [one] minute
   *[other] minutes
}: { $reason }

## timeout / untimeout / timeouts
cmd-timeout-desc = Temporarily blocks a player from all chat (say, whisper, emote, OOC, LOOC, dead chat, GIFs).
cmd-timeout-help = Usage: timeout <player> <minutes> [reason]
cmd-untimeout-desc = Lifts a chat timeout early.
cmd-untimeout-help = Usage: untimeout <player>
cmd-timeouts-desc = Lists active chat timeouts.
cmd-timeouts-help = Usage: timeouts
cmd-timeout-hint-player = <player>
cmd-timeout-invalid-minutes = "{ $minutes }" is not a valid number of minutes.
cmd-timeout-default-reason = No reason given.
cmd-timeout-player-not-found = Unable to find a player named "{ $player }".
cmd-timeout-set = { $player } is timed out from chat for { $minutes } minutes: { $reason }
cmd-timeout-removed = Chat timeout on { $player } lifted.
cmd-timeout-none = { $player } has no active chat timeout.
cmd-timeout-list-empty = No active chat timeouts.
cmd-timeout-list-entry = { $player }: { $minutes } min left — { $reason }
cmd-timeout-notice-target = An admin has timed you out from chat for { $minutes } minutes: { $reason }
cmd-timeout-notice-lifted = Your chat timeout has been lifted.

## gifmute / gifunmute / gifmutes
cmd-gifmute-desc = Temporarily blocks a player from sending GIFs in chat.
cmd-gifmute-help = Usage: gifmute <player> <minutes> [reason]
cmd-gifunmute-desc = Lifts a GIF mute early.
cmd-gifunmute-help = Usage: gifunmute <player>
cmd-gifmutes-desc = Lists active GIF mutes.
cmd-gifmutes-help = Usage: gifmutes
cmd-gifmute-hint-player = <player>
cmd-gifmute-invalid-minutes = "{ $minutes }" is not a valid number of minutes.
cmd-gifmute-default-reason = No reason given.
cmd-gifmute-player-not-found = Unable to find a player named "{ $player }".
cmd-gifmute-set = { $player } is GIF-muted for { $minutes } minutes: { $reason }
cmd-gifmute-removed = GIF mute on { $player } lifted.
cmd-gifmute-none = { $player } has no active GIF mute.
cmd-gifmute-list-empty = No active GIF mutes.
cmd-gifmute-list-entry = { $player }: { $minutes } min left — { $reason }
cmd-gifmute-notice-target = An admin has GIF-muted you for { $minutes } minutes: { $reason }
cmd-gifmute-notice-lifted = Your GIF mute has been lifted.

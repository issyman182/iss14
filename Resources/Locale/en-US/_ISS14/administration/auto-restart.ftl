cmd-autorestartconfig-desc = Opens the scheduled auto-restart configuration window.
cmd-autorestartconfig-help = Usage: autorestartconfig
cmd-autorestartconfig-server = This command cannot be run from the server console.

auto-restart-config-title = Auto Restart Config
auto-restart-config-settings = Settings
auto-restart-config-enabled = Enable scheduled automatic restart
auto-restart-config-time = Restart time (24h, HH:MM):
auto-restart-config-apply = Apply
auto-restart-config-clock = Schedule
auto-restart-config-server-time = Server time: {$time}
auto-restart-config-next = Next restart: {$time} (in {$remaining})
auto-restart-config-next-none = Next restart: —
auto-restart-config-armed = The server will restart gracefully at the end of the round after the scheduled time.
auto-restart-config-disabled = Automatic restart is disabled.
auto-restart-config-pending = Restart time reached — the server will restart at the end of the current round.
auto-restart-config-hint = The restart is graceful: it waits for the current round to end. The service manager (e.g. systemd with Restart=always) must restart the server process.

# iss14: Economy
salary-notice = Salary: { $credits } credits paid for this shift as { $jobs } (presence factor { $factor }).

cmd-bank-desc = Shows a player's bank balance.
cmd-bank-help = Usage: bank <player>
cmd-bank-invalid-arguments = Usage: bank <player>
cmd-bank-invalid-player = No player found with that name or user id.
cmd-bank-result = { $player }: { $balance } credits
cmd-bank-completion-player = <player>

cmd-bankset-desc = Sets a player's bank balance.
cmd-bankset-help = Usage: bankset <player> <amount>
cmd-bankset-invalid-arguments = Usage: bankset <player> <amount>
cmd-bankset-completion-amount = <amount>

cmd-bankadd-desc = Adds (or with a negative delta removes) credits to a player's bank account.
cmd-bankadd-help = Usage: bankadd <player> <delta> [reason]
cmd-bankadd-invalid-arguments = Usage: bankadd <player> <delta> [reason]
cmd-bankadd-negative = The balance cannot go below zero.
cmd-bankadd-completion-delta = <delta>
cmd-bankadd-completion-reason = [reason]

cmd-banktop-desc = Lists the richest bank accounts.
cmd-banktop-help = Usage: banktop [count]
cmd-banktop-invalid-arguments = Usage: banktop [count]
cmd-banktop-empty = No bank accounts yet.
cmd-banktop-row = #{ $rank } { $player }: { $balance } credits (lifetime earned { $earned })
cmd-banktop-completion-count = [count]

cmd-balance-desc = Shows your own bank balance.
cmd-balance-help = Usage: balance
cmd-balance-server = Only players have a bank account.
cmd-balance-result = Your balance: { $balance } credits

cmd-banklog-desc = Lists the newest bank transactions of a player or a department account (e.g. Service).
cmd-banklog-help = Usage: banklog <player|account> [count]
cmd-banklog-invalid-arguments = Usage: banklog <player|account> [count]
cmd-banklog-empty = No transactions found.
cmd-banklog-row = [{ $time }] round { $round }: { $from } -> { $to } { $amount } credits: { $reason }
cmd-banklog-completion-party = <player or account>
cmd-banklog-completion-count = [count]

salary-round-end-summary = Salaries paid to { $count } crew members: { $total } credits in total.

economy-party-player-unknown = unknown account
economy-party-station = station budget
economy-party-external = CentComm
economy-error-invalid-amount = Invalid amount.
economy-error-no-account = No account found.
economy-error-insufficient-funds = Insufficient funds.
economy-error-account-full = The receiving account is full.
economy-error-no-access = You are not authorized to use this account.
economy-error-fingerprint = Fingerprint does not match the card holder.

heist-notice = Heist: { $credits } credits were paid to your account for loot worth { $loot }{ $bonus ->
    [0] .
   *[other] { " " }plus { $bonus } credits for completed thefts.
}
heist-round-end-header = [color=orange]Heists:[/color]
heist-round-end-line = { $name } escaped with loot worth { $loot } credits and was paid { $credits } credits.

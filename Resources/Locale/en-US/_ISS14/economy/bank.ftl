# iss14: Economy
salary-notice = Salary: { $credits } credits for { $minutes } min as { $job }.

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

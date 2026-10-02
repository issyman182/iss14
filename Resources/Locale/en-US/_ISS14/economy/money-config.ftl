# iss14: Economy
cmd-moneyconfig-desc = Opens the economy configuration panel.
cmd-moneyconfig-help = Usage: moneyconfig
cmd-moneyconfig-server = Only players can open the money config panel.

money-config-title = Money Config
money-config-tab-settings = Settings
money-config-tab-salaries = Salaries
money-config-invalid = Invalid value.

money-config-bank = Bank
money-config-max-balance = Max balance (credits)

money-config-salary = Salary
money-config-salary-enabled = Pay salaries at round end
money-config-salary-default = Default base salary per round (unlisted jobs)
money-config-salary-multiplier = Salary multiplier
money-config-salary-min-factor = Minimum factor (barely present)
money-config-salary-full-fraction = Share of the round needed for the full salary
money-config-salary-max-factor = Maximum factor (present the whole round)
money-config-fines-from-salary = Deduct unpaid fines from the salary

money-config-salary-table-hint = Base salary per round for each job. Edit a value to override the salary table; Reset returns to the default.
money-config-salary-reset = Reset
money-config-salary-default-hint = (default { $default })
money-config-salary-no-department = Other

money-config-atm = ATM & ID card payments
money-config-atm-fingerprint = Require matching fingerprint for ID card payments
money-config-atm-limit = ATM withdrawal limit per round (0 = none)
money-config-atm-fee = ATM withdrawal fee

money-config-vending = Vending machines
money-config-vending-prices = Vending machines charge the ID card's account
money-config-vending-multiplier = Price multiplier

money-config-service = Cash registers
money-config-service-multiplier = Default price multiplier (appraised value × this)
money-config-service-commission = Cashier commission

money-config-chips = Poker chips
money-config-chip-rate = Credits per chip
money-config-chip-fee = Cash-out fee
money-config-chip-limit = Cash-out limit per round (0 = none)

money-config-cargo = Cargo
money-config-cargo-private = Allow privately paid orders
money-config-cargo-markup = Private order price multiplier
money-config-cargo-blocklist = Enforce the private order blocklist

money-config-legal = Fines & lawyers
money-config-lawyer-fee = Lawyer fee (share of a fine paid for a client)

money-config-heist = Antagonist heists
money-config-heist-enabled = Pay antagonists for loot carried onto the evac shuttle
money-config-heist-factor = Payout per credit of loot value
money-config-heist-min-value = Minimum item value to count as loot (cash always counts)
money-config-heist-thief-bonus = Thief bonus per completed steal objective

money-config-store = Store
money-config-store-enabled = Store (titles, loadout unlocks) enabled

money-config-tasks = Department tasks
money-config-tasks-enabled = Task boards generate tasks
money-config-task-multiplier = Task reward multiplier
money-config-task-interval = Seconds between generated tasks per board

money-config-hint = Values apply immediately. Run "saveconfig" to write them to server_config.toml. Salary overrides are stored in the database.

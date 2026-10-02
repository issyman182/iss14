# iss14: Economy
cash-register-window-title = Cash Register
cash-register-tab-sale = Sale
cash-register-tab-prices = Prices
cash-register-refresh = Refresh
cash-register-department = Account: { $department } ({ $balance } credits) · cashier commission { $commission }%
cash-register-items-hint = Items on the counter around the register. Pick how many of each go on the bill.
cash-register-prices-hint = Prices per piece. Empty or Reset returns to the default (appraised value).
cash-register-no-items = Nothing sellable lies near the register.
cash-register-item-info = { $count } available · { $price } cr
cash-register-all = all
cash-register-selection-total = Selected: { $total } credits
cash-register-create-bill = Write bill
cash-register-cancel-bill = Cancel bill
cash-register-no-open-bill = No open bill.
cash-register-open-bill = Open bill: { $total } credits (by { $cashier })
cash-register-bill-line = { $count }x { $name } @ { $price } = { $total }
cash-register-reset-price = Reset
cash-register-default-price = default { $price }
cash-register-access-denied = The register only responds to service staff.

cash-register-empty-bill = Nothing selected for the bill.
cash-register-bill-created = Bill written: { $total } credits. The customer can now swipe their ID or pay cash.
cash-register-bill-popup = Bill: { $total } credits
cash-register-bill-cancelled = Bill cancelled.
cash-register-no-bill = There is no open bill to pay.
cash-register-no-station = The register is not connected to a station account.
cash-register-not-enough-cash = The bill is { $total } credits, you handed over { $given }.
cash-register-paid-card = Paid { $total } credits by card. Thank you!
cash-register-paid-cash = Paid { $total } credits in cash{ $change ->
    [0] .
   *[other] , { $change } credits change.
}
cash-register-examine-bill = There is an open bill of [color=yellow]{ $total } credits[/color] written by { $cashier }. Swipe your ID or hand over cash to pay.
cash-register-examine-idle = No open bill.

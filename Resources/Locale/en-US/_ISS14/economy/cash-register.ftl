# iss14: Economy
cash-register-window-title = Cash Register
cash-register-tab-sale = Sale
cash-register-tab-prices = Prices
cash-register-refresh = Refresh
cash-register-department = Account: { $department } ({ $balance } credits)
cash-register-commission = Cashier commission: { $commission }%
cash-register-items-hint = Your catalog and whatever lies on the counter.
cash-register-items-hint-2 = Use + / −, click the register with an item in hand to scan it, or type something in below.
cash-register-prices-hint = Prices per piece for this register. Reset returns to the station default (set by admins in the money config, else the appraised value).
cash-register-search = Search...
cash-register-nearby-only = Counter only
cash-register-no-items = Nothing sellable in your catalog or near the register.
cash-register-no-nearby = Nothing sellable lies on the counter. Untick "Counter only" to see the whole catalog.
cash-register-filter = Filter...
cash-register-filter-title = Catalog filter
cash-register-filter-hint = Untick a catalog to hide its items.
cash-register-filter-active = Filter on: showing { $catalogs }
cash-register-filter-none = Filter on: all catalogs hidden
cash-register-manual-hint = Not on the list (a mixed drink, a special)? Type it in:
cash-register-manual-name = Name
cash-register-manual-price = Price
cash-register-manual-add = Add
cash-register-manual-invalid = Enter a name, a price above 0 and a count.
cash-register-no-matches = Nothing matches your search.
cash-register-item-info = { $count } on counter · { $price } cr
cash-register-item-price = { $price } cr
cash-register-all = all
cash-register-selection-total = Total: { $total } credits
cash-register-draft-empty = Bill is empty.
cash-register-draft-header = Current bill
cash-register-clear-draft = Clear
cash-register-create-bill = Write bill
cash-register-cancel-bill = Cancel bill
cash-register-no-open-bill = No open bill.
cash-register-open-bill = Open bill: { $total } credits (by { $cashier })
cash-register-bill-line = { $count }x { $name } @ { $price } = { $total }
cash-register-reset-price = Reset
cash-register-default-price = default { $price }
cash-register-access-denied = The register only responds to staff.
cash-register-no-power = The register has no power.
cash-register-empty-drink = { $item } is empty. Nobody pays for an empty glass.
cash-register-examine-unpowered = It is [color=red]unpowered[/color].
cash-register-catalog-nearby = On the counter
cash-register-catalog-custom = Custom prices
cash-register-catalog-bar = Bar
cash-register-catalog-kitchen = Kitchen
cash-register-catalog-hydroponics = Hydroponics
cash-register-catalog-medical = Medical

# Customer view
cash-register-customer-department = Paying to: { $department }
cash-register-customer-no-bill = No open bill — ask the staff to ring you up.
cash-register-customer-bill = Your bill (written by { $cashier })
cash-register-customer-total = Total: { $total } credits
cash-register-customer-hint = Pay with the ID card you carry, or swipe your ID / PDA or hand over cash directly.
cash-register-pay-card = Pay with ID
cash-register-pay-card-total = Pay { $total } credits with ID

cash-register-empty-bill = Nothing on the bill.
cash-register-bill-already-open = There is already an open bill. Cancel it or let the customer pay first.
cash-register-bill-created = Bill written: { $total } credits. The customer can now pay.
cash-register-bill-popup = Bill: { $total } credits
cash-register-bill-cancelled = Bill cancelled.
cash-register-no-bill = There is no open bill to pay.
cash-register-no-card = You carry no ID card to pay with.
cash-register-no-station = The register is not connected to a station account.
cash-register-not-sellable = { $item } is not something this register sells.
cash-register-worthless = { $item } has no price. Set one in the price list first.
cash-register-scanned = Added { $count }x { $item } ({ $price } cr) — bill: { $total } cr
cash-register-not-enough-cash = The bill is { $total } credits, you handed over { $given }.
cash-register-paid-card = Paid { $total } credits by card. Thank you!
cash-register-paid-cash = Paid { $total } credits in cash{ $change ->
    [0] .
   *[other] , { $change } credits change.
}
cash-register-examine-bill = There is an open bill of [color=yellow]{ $total } credits[/color] written by { $cashier }. Click the register to pay, or swipe your ID or hand over cash.
cash-register-examine-idle = No open bill.

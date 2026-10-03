# iss14: Economy — privately paid cargo orders
cargo-console-menu-order-row-button-private = Pay privately
cargo-console-menu-order-row-button-private-tooltip = Approve this order and pay { $cost } spesos from your own bank account (ID card required).
cargo-console-menu-private-policy-button = Edit blocklist...
cargo-console-menu-private-policy-hint = [bold]Private order blocklist[/bold] — products crew may not buy with their own money this shift. Only Logistics can change it.

# Order form
cargo-console-order-menu-payment-label = Payment:
cargo-console-order-menu-payment-department = { $account } account — requested, Logistics approves
cargo-console-order-menu-payment-private = Private — pay { $cost } spesos now from your own account
cargo-console-order-menu-payment-hint-department = The order is put on the request list; the department pays once Logistics approves it.
cargo-console-order-menu-payment-hint-private = Charged to the ID card you carry (fingerprint checked). Delivered without approval. Markup: ×{ $markup }.
cargo-console-order-menu-payment-hint-blocked = Logistics does not sell this product to private customers.

# Transfers tab (upstream feature, explained)
cargo-console-menu-funds-hint = [bold]Account transfers.[/bold] Withdraw this department's money as cash or move it to another department. Each action is limited to a share of the balance and has a cooldown; the department head can lift the limit below.
cargo-console-menu-toggle-account-lock-hint = Department head only: lift or restore the per-action transfer limit.
cargo-console-menu-toggle-account-lock-button-restore = Restore Transfer Limit

cargo-console-private-disabled = Private orders are disabled on this server.
cargo-console-private-blocked = Logistics does not sell this to private customers.
cargo-console-private-no-card = You need an ID card bound to a bank account to pay privately.
cargo-console-private-insufficient = Insufficient funds: this order costs { $cost } spesos privately.
cargo-console-private-paid = Paid { $cost } spesos from your account. Logistics will deliver your order.
cargo-console-private-approved-broadcast = Order of {$orderAmount} {$productName} privately paid by {$approver} for {$cost} spesos.
cargo-console-private-policy-cargo-only = The private order policy can only be changed from a Logistics console.

cargo-private-policy-title = Private Order Blocklist
cargo-private-policy-hint = Checked products cannot be bought with private money this shift. Tick a category to block everything in it.
cargo-private-policy-search = Search products or categories...
cargo-private-policy-status-on = Blocklist: ON · { $count } products blocked
cargo-private-policy-status-off = Blocklist: OFF · nothing blocked ({ $count } ticked)
cargo-private-policy-enable = Turn on
cargo-private-policy-disable = Turn off
cargo-private-policy-no-station = No station found.
cargo-private-policy-category = { $category } ({ $blocked }/{ $total } blocked)
cargo-private-policy-category-tooltip = Block or allow every product in this category.

using Content.Client.Cargo.UI;
using Content.Shared.Cargo;
using Content.Shared.Cargo.BUI;
using Content.Shared.Cargo.Components;
using Content.Shared.Cargo.Events;
using Content.Shared.Cargo.Prototypes;
using Content.Shared.IdentityManagement;
using Robust.Client.GameObjects;
using Robust.Client.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client.Cargo.BUI
{
    public sealed partial class CargoOrderConsoleBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
    {
        [Dependency] private SharedCargoSystem _cargoSystem = default!;
        [Dependency] private IdentitySystem _identity = default!;
        [Dependency] private Robust.Shared.Configuration.IConfigurationManager _cfg = default!; // iss14: Economy

        [ViewVariables]
        private CargoConsoleMenu? _menu;

        /// <summary>
        /// This is the separate popup window for individual orders.
        /// </summary>
        [ViewVariables]
        private CargoConsoleOrderMenu? _orderMenu;

        [ViewVariables]
        public string? AccountName { get; private set; }

        [ViewVariables]
        public int BankBalance { get; private set; }

        [ViewVariables]
        public int OrderCapacity { get; private set; }

        [ViewVariables]
        public int OrderCount { get; private set; }

        /// <summary>
        /// Currently selected product
        /// </summary>
        [ViewVariables]
        private CargoProductPrototype? _product;

        protected override void Open()
        {
            base.Open();

            var spriteSystem = EntMan.System<SpriteSystem>();
            var dependencies = IoCManager.Instance!;
            _menu = new CargoConsoleMenu(Owner, EntMan, dependencies.Resolve<IPrototypeManager>(), spriteSystem);
            var localPlayer = dependencies.Resolve<IPlayerManager>().LocalEntity;
            var description = new FormattedMessage();

            var orderRequester = Loc.GetString("cargo-console-paper-approver-default");
            if (EntMan.EntityExists(localPlayer))
                orderRequester = _identity.GetIdentityShortInfo(localPlayer.Value, Owner) ?? orderRequester;

            _orderMenu = new CargoConsoleOrderMenu();

            _menu.OnClose += Close;

            _menu.OnItemSelected += (row) =>
            {
                if (row == null)
                    return;

                description.Clear();
                description.PushColor(Color.White); // Rich text default color is grey
                if (row.MainButton.ToolTip != null)
                    description.AddText(row.MainButton.ToolTip);

                _orderMenu.Description.SetMessage(description);
                _product = row.Product;
                _orderMenu.ProductName.Text = row.ProductName.Text;
                _orderMenu.PointCost.Text = row.PointCost.Text;
                _orderMenu.Requester.Text = orderRequester;
                _orderMenu.Reason.Text = "";
                _orderMenu.Amount.Value = 1;
                SetupPayment(); // iss14: Economy

                _orderMenu.OpenCentered();
                _orderMenu.SetPositionLast();
            };
            _menu.OnOrderApproved += ApproveOrder;
            _menu.OnOrderCanceled += RemoveOrder;
            // iss14: Economy
            _menu.OnOrderPaidPrivately += order =>
            {
                if (order != null)
                    SendMessage(new Content.Shared.Economy.CargoConsoleApprovePrivateOrderMessage(order.OrderId));
            };
            _menu.OnPrivatePolicy += OpenPrivatePolicy;
            _orderMenu.SubmitButton.OnPressed += (_) =>
            {
                if (AddOrder())
                {
                    _orderMenu.Close();
                }
            };
            // iss14: Economy
            _orderMenu.Payment.OnItemSelected += args =>
            {
                _orderMenu.Payment.SelectId(args.Id);
                UpdatePaymentHint();
            };
            _orderMenu.Amount.ValueChanged += _ => UpdatePaymentHint();

            _menu.OnAccountAction += (account, amount) =>
            {
                SendMessage(new CargoConsoleWithdrawFundsMessage(account, amount));
            };

            _menu.OnToggleUnboundedLimit += _ =>
            {
                SendMessage(new CargoConsoleToggleLimitMessage());
            };

            _menu.OpenCentered();
        }

        private void Populate(List<CargoOrderData> orders)
        {
            if (_menu == null)
                return;

            _menu.PopulateProducts();
            _menu.PopulateCategories();
            _menu.PopulateOrders(orders);
            _menu.PopulateAccountActions();
        }

        protected override void UpdateState(BoundUserInterfaceState state)
        {
            base.UpdateState(state);

            if (state is not CargoConsoleInterfaceState cState || !EntMan.TryGetComponent<CargoOrderConsoleComponent>(Owner, out var orderConsole))
                return;
            var station = EntMan.GetEntity(cState.Station);

            OrderCapacity = cState.Capacity;
            OrderCount = cState.Count;
            BankBalance = _cargoSystem.GetBalanceFromAccount(station, orderConsole.Account);

            AccountName = cState.Name;

            if (_menu == null)
                return;

            _menu.ProductCatalogue = cState.Products;
            _menu.ShuttleCapacityLabel.Text = Loc.GetString(
                "cargo-console-menu-order-capacity-number",
                ("count", OrderCount),
                ("capacity", OrderCapacity)
            );

            _menu?.UpdateStation(station);
            _station = station; // iss14: Economy
            _policyWindow?.SetStation(station);
            Populate(cState.Orders);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (!disposing)
                return;

            _menu?.Dispose();
            _orderMenu?.Dispose();
            _policyWindow?.Dispose(); // iss14: Economy
        }

        // iss14: Economy — private order blocklist editor
        [ViewVariables]
        private CargoPrivatePolicyWindow? _policyWindow;

        private EntityUid? _station;

        private void OpenPrivatePolicy()
        {
            if (_station == null)
                return;

            if (_policyWindow == null)
            {
                _policyWindow = new CargoPrivatePolicyWindow(EntMan, IoCManager.Resolve<IPrototypeManager>());
                _policyWindow.OnSetBlocked += (products, blocked) => SendMessage(new Content.Shared.Economy.CargoConsoleSetPrivateBlockMessage(products, blocked));
                _policyWindow.OnToggle += () => SendMessage(new Content.Shared.Economy.CargoConsoleTogglePrivateBlocklistMessage());
                _policyWindow.OnClose += () => _policyWindow = null;
            }

            _policyWindow.SetStation(_station.Value);
            _policyWindow.OpenCentered();
        }

        private bool AddOrder()
        {
            var orderAmt = _orderMenu?.Amount.Value ?? 0;
            if (orderAmt < 1 || orderAmt > OrderCapacity)
            {
                return false;
            }

            // iss14: Economy — private payment: created and paid right away, no approval.
            if (_orderMenu?.Payment.SelectedId == PaymentPrivate)
            {
                SendMessage(new Content.Shared.Economy.CargoConsoleAddPrivateOrderMessage(
                    _orderMenu.Requester.Text,
                    _orderMenu.Reason.Text,
                    _product?.ID ?? "",
                    orderAmt));
                return true;
            }

            SendMessage(new CargoConsoleAddOrderMessage(
                _orderMenu?.Requester.Text ?? "",
                _orderMenu?.Reason.Text ?? "",
                _product?.ID ?? "",
                orderAmt));

            return true;
        }

        // iss14: Economy — payment selector on the order form
        private const int PaymentDepartment = 0;
        private const int PaymentPrivate = 1;

        private void SetupPayment()
        {
            if (_orderMenu == null || !EntMan.TryGetComponent<CargoOrderConsoleComponent>(Owner, out var console))
                return;

            var protoMan = IoCManager.Resolve<IPrototypeManager>();
            var accountName = protoMan.TryIndex(console.Account, out var accountProto) ? Loc.GetString(accountProto.Name) : console.Account.Id;

            _orderMenu.Payment.Clear();
            _orderMenu.Payment.AddItem(Loc.GetString("cargo-console-order-menu-payment-department", ("account", accountName)), PaymentDepartment);

            var privateAllowed = _cfg.GetCVar(Content.Shared.CCVar.CCVars.EconomyCargoPrivateOrders)
                                 && console.Mode != CargoOrderConsoleMode.PrintSlip;
            if (privateAllowed)
                _orderMenu.Payment.AddItem(PrivateOptionText(), PaymentPrivate);

            _orderMenu.Payment.SelectId(PaymentDepartment);
            _orderMenu.Payment.Disabled = !privateAllowed;
            UpdatePaymentHint();
        }

        private int PrivateCost()
        {
            var markup = _cfg.GetCVar(Content.Shared.CCVar.CCVars.EconomyCargoPrivateMarkup);
            var amount = Math.Max(1, _orderMenu?.Amount.Value ?? 1);
            return (int) Math.Ceiling((_product?.Cost ?? 0) * amount * markup);
        }

        private string PrivateOptionText() => Loc.GetString("cargo-console-order-menu-payment-private", ("cost", PrivateCost()));

        private bool IsPrivatelyBlocked()
        {
            if (_product == null || _station == null || !_cfg.GetCVar(Content.Shared.CCVar.CCVars.EconomyCargoPrivateBlocklist))
                return false;

            return EntMan.TryGetComponent<Content.Shared.Economy.CargoPrivateOrderPolicyComponent>(_station.Value, out var policy)
                   && policy.BlocklistEnabled && policy.Blocked.Contains(_product.ID);
        }

        private void UpdatePaymentHint()
        {
            if (_orderMenu == null)
                return;

            // Keep the private option's price in step with the amount.
            for (var i = 0; i < _orderMenu.Payment.ItemCount; i++)
            {
                if (_orderMenu.Payment.GetItemId(i) == PaymentPrivate)
                    _orderMenu.Payment.SetItemText(i, PrivateOptionText());
            }

            if (_orderMenu.Payment.SelectedId == PaymentPrivate)
            {
                var blocked = IsPrivatelyBlocked();
                _orderMenu.PaymentHint.Text = blocked
                    ? Loc.GetString("cargo-console-order-menu-payment-hint-blocked")
                    : Loc.GetString("cargo-console-order-menu-payment-hint-private",
                        ("markup", _cfg.GetCVar(Content.Shared.CCVar.CCVars.EconomyCargoPrivateMarkup).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)));
                _orderMenu.SubmitButton.Disabled = blocked;
            }
            else
            {
                _orderMenu.PaymentHint.Text = Loc.GetString("cargo-console-order-menu-payment-hint-department");
                _orderMenu.SubmitButton.Disabled = false;
            }
        }

        private void RemoveOrder(CargoOrderData? order)
        {
            if (order == null)
                return;

            SendMessage(new CargoConsoleRemoveOrderMessage(order.OrderId));
        }

        private void ApproveOrder(CargoOrderData? order)
        {
            if (order == null)
                return;

            if (OrderCount >= OrderCapacity)
                return;

            SendMessage(new CargoConsoleApproveOrderMessage(order.OrderId));
        }
    }
}

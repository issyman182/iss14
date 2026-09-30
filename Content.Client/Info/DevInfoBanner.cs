using Content.Client.Credits;
using Content.Client.Stylesheets;
using Content.Shared.CCVar;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared;
using Robust.Shared.Configuration;

namespace Content.Client.Info
{
    public sealed class DevInfoBanner : BoxContainer
    {
        private readonly IConfigurationManager _cfg;
        private readonly Label _versionLabel;

        public DevInfoBanner() {
            var buttons = new BoxContainer
            {
                Orientation = LayoutOrientation.Horizontal
            };
            AddChild(buttons);

            var uriOpener = IoCManager.Resolve<IUriOpener>();
            _cfg = IoCManager.Resolve<IConfigurationManager>();

            var bugReport = _cfg.GetCVar(CCVars.InfoLinksBugReport);
            if (!string.IsNullOrWhiteSpace(bugReport))
            {
                var reportButton = new Button {Text = Loc.GetString("server-info-report-button")};
                reportButton.OnPressed += _ => uriOpener.OpenUri(bugReport);
                buttons.AddChild(reportButton);
            }

            var creditsButton = new Button {Text = Loc.GetString("server-info-credits-button")};
            creditsButton.OnPressed += _ => new CreditsWindow().Open();
            buttons.AddChild(creditsButton);

            // Fork version tag (e.g. "iss14:1.5.0"), pulled from the build CVars which the
            // server populates from server_config.toml [build] (fork_id/version) and replicates
            // to the client. Refreshed on tree-enter and on CVar change, since the banner can be
            // built before the replicated values arrive. Hidden when unset (e.g. test builds).
            _versionLabel = new Label
            {
                StyleClasses = { StyleClass.LabelSubText },
                VerticalAlignment = VAlignment.Center,
                Margin = new Thickness(8, 0, 4, 0),
                Visible = false,
            };
            buttons.AddChild(_versionLabel);
        }

        protected override void EnteredTree()
        {
            base.EnteredTree();

            _cfg.OnValueChanged(CVars.BuildForkId, OnVersionCVarChanged);
            _cfg.OnValueChanged(CVars.BuildVersion, OnVersionCVarChanged);
            UpdateVersion();
        }

        protected override void ExitedTree()
        {
            base.ExitedTree();

            _cfg.UnsubValueChanged(CVars.BuildForkId, OnVersionCVarChanged);
            _cfg.UnsubValueChanged(CVars.BuildVersion, OnVersionCVarChanged);
        }

        private void OnVersionCVarChanged(string _) => UpdateVersion();

        private void UpdateVersion()
        {
            var fork = _cfg.GetCVar(CVars.BuildForkId);
            var version = _cfg.GetCVar(CVars.BuildVersion);

            if (string.IsNullOrEmpty(fork) || string.IsNullOrEmpty(version))
            {
                _versionLabel.Visible = false;
                return;
            }

            _versionLabel.Text = Loc.GetString("server-info-version", ("fork", fork), ("version", version));
            _versionLabel.Visible = true;
        }
    }
}

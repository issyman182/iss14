using Content.Client.Gameplay;
using Content.Client.Ghost;
using Content.Client.UserInterface.Systems.Gameplay;
using Content.Client.UserInterface.Systems.Ghost.Widgets;
using Content.Shared.Ghost.Components;
using Content.Shared.Ghost.Systems;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controllers;
using Robust.Shared.Timing;

namespace Content.Client.UserInterface.Systems.Ghost;

// TODO hud refactor BEFORE MERGE fix ghost gui being too far up
public sealed partial class GhostUIController : UIController, IOnSystemChanged<GhostSystem>
{
    [Dependency] private IEntityNetworkManager _net = default!;
    [Dependency] private IGameTiming _timing = default!;

    [UISystemDependency] private readonly GhostSystem? _system = default;

    private GhostGui? Gui => UIManager.GetActiveUIWidgetOrNull<GhostGui>();

    public override void Initialize()
    {
        base.Initialize();

        var gameplayStateLoad = UIManager.GetUIController<GameplayStateLoadController>();
        gameplayStateLoad.OnScreenLoad += OnScreenLoad;
        gameplayStateLoad.OnScreenUnload += OnScreenUnload;
    }

    private void OnScreenLoad()
    {
        LoadGui();
    }

    private void OnScreenUnload()
    {
        UnloadGui();
    }

    public void OnSystemLoaded(GhostSystem system)
    {
        system.PlayerRemoved += OnPlayerRemoved;
        system.PlayerUpdated += OnPlayerUpdated;
        system.PlayerAttached += OnPlayerAttached;
        system.PlayerDetached += OnPlayerDetached;
        system.GhostWarpsResponse += OnWarpsResponse;
        system.GhostRoleCountUpdated += OnRoleCountUpdated;
    }

    public void OnSystemUnloaded(GhostSystem system)
    {
        system.PlayerRemoved -= OnPlayerRemoved;
        system.PlayerUpdated -= OnPlayerUpdated;
        system.PlayerAttached -= OnPlayerAttached;
        system.PlayerDetached -= OnPlayerDetached;
        system.GhostWarpsResponse -= OnWarpsResponse;
        system.GhostRoleCountUpdated -= OnRoleCountUpdated;
    }

    public void UpdateGui()
    {
        if (Gui == null)
        {
            return;
        }

        Gui.Visible = _system?.IsGhost ?? false;
        Gui.Update(_system?.AvailableGhostRoleCount, _system?.Player?.CanReturnToBody, BuildNewLifeInfo());
    }

    private GhostNewLifeInfo BuildNewLifeInfo()
    {
        var ghost = _system?.Player;
        // Hidden when feature off / no lives left (server reports remaining == 0 in those cases).
        if (ghost == null || ghost.NewLivesRemaining == 0)
            return new GhostNewLifeInfo { Show = false };

        var info = new GhostNewLifeInfo { Show = true };
        var remainingText = ghost.NewLivesRemaining < 0
            ? string.Empty
            : $" ({ghost.NewLivesRemaining})";

        var cooldownLeft = ghost.NewLifeEligibleTime - _timing.CurTime;
        if (cooldownLeft > TimeSpan.Zero)
        {
            info.Enabled = false;
            info.Text = Loc.GetString("new-life-button-cooldown", ("seconds", (int) Math.Ceiling(cooldownLeft.TotalSeconds)));
        }
        else if (!ghost.NewLifeAllowed)
        {
            info.Enabled = false;
            info.Text = Loc.GetString("new-life-button-blocked");
        }
        else
        {
            info.Enabled = true;
            info.Text = Loc.GetString("new-life-button-ready") + remainingText;
        }

        return info;
    }

    private void OnPlayerRemoved(GhostComponent component)
    {
        Gui?.Hide();
    }

    private void OnPlayerUpdated(GhostComponent component)
    {
        UpdateGui();
    }

    private void OnPlayerAttached(GhostComponent component)
    {
        if (Gui == null)
            return;

        Gui.Visible = true;
        UpdateGui();
    }

    private void OnPlayerDetached()
    {
        Gui?.Hide();
    }

    private void OnWarpsResponse(GhostWarpsResponseEvent msg)
    {
        if (Gui?.TargetWindow is not { } window)
            return;

        window.UpdateWarps(msg.Warps);
        window.Populate();
    }

    private void OnRoleCountUpdated(GhostUpdateGhostRoleCountEvent msg)
    {
        UpdateGui();
    }

    private void OnWarpClicked(NetEntity player)
    {
        var msg = new GhostWarpToTargetRequestEvent(player);
        _net.SendSystemNetworkMessage(msg);
    }

    private void OnGhostnadoClicked()
    {
        var msg = new GhostnadoRequestEvent();
        _net.SendSystemNetworkMessage(msg);
    }

    private void OnWarpToRandomFollowedClicked()
    {
        var msg = new WarpToRandomFollowedRequestEvent();
        _net.SendSystemNetworkMessage(msg);
    }

    private void OnWarpToRandomClicked()
    {
        var msg = new WarpToRandomRequestEvent();
        _net.SendSystemNetworkMessage(msg);
    }

    public void LoadGui()
    {
        if (Gui == null)
            return;

        Gui.RequestWarpsPressed += RequestWarps;
        Gui.ReturnToBodyPressed += ReturnToBody;
        Gui.GhostRolesPressed += GhostRolesPressed;
        Gui.NewLifePressed += NewLife;
        Gui.TargetWindow.WarpClicked += OnWarpClicked;
        Gui.TargetWindow.OnGhostnadoClicked += OnGhostnadoClicked;
        Gui.TargetWindow.OnWarpToRandomFollowedClicked += OnWarpToRandomFollowedClicked;
        Gui.TargetWindow.OnWarpToRandomClicked += OnWarpToRandomClicked;

        UpdateGui();
    }

    public void UnloadGui()
    {
        if (Gui == null)
            return;

        Gui.RequestWarpsPressed -= RequestWarps;
        Gui.ReturnToBodyPressed -= ReturnToBody;
        Gui.GhostRolesPressed -= GhostRolesPressed;
        Gui.NewLifePressed -= NewLife;
        Gui.TargetWindow.WarpClicked -= OnWarpClicked;

        Gui.Hide();
    }

    private void ReturnToBody()
    {
        _system?.ReturnToBody();
    }

    private void NewLife()
    {
        _system?.RequestNewLife();
    }

    private void RequestWarps()
    {
        _system?.RequestWarps();
        Gui?.TargetWindow.Populate();
        Gui?.TargetWindow.OpenCentered();
    }

    private void GhostRolesPressed()
    {
        _system?.OpenGhostRoles();
    }
}

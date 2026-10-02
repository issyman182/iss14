// iss14: Economy
using Content.Shared.Economy.Store;
using Robust.Client;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Client.Economy.Store;

/// <summary>Client mirror of the player's store state (balance, owned items, worn title).</summary>
public sealed partial class ClientStoreManager : ISharedStoreManager
{
    [Dependency] private IClientNetManager _net = default!;
    [Dependency] private IBaseClient _client = default!;

    public long Balance { get; private set; }
    public IReadOnlySet<string> Owned => _owned;
    public string? Title { get; private set; }
    public string Status { get; private set; } = string.Empty;

    private readonly HashSet<string> _owned = new();

    public event Action? Updated;

    public void Initialize()
    {
        _net.RegisterNetMessage<MsgStoreState>(RxState);
        _net.RegisterNetMessage<MsgStoreBuy>();
        _net.RegisterNetMessage<MsgStoreSetTitle>();
        _net.RegisterNetMessage<MsgStoreRequest>();

        _client.RunLevelChanged += (_, e) =>
        {
            if (e.NewLevel == ClientRunLevel.Initialize)
            {
                _owned.Clear();
                Balance = 0;
                Title = null;
                Status = string.Empty;
                Updated?.Invoke();
            }
        };
    }

    private void RxState(MsgStoreState msg)
    {
        Balance = msg.Balance;
        _owned.Clear();
        _owned.UnionWith(msg.Owned);
        Title = msg.Title;
        Status = msg.Status;
        Updated?.Invoke();
    }

    public bool HasUnlock(ICommonSession session, string item) => _owned.Contains(item);

    public void Buy(string item) => _net.ClientSendMessage(new MsgStoreBuy { Item = item });

    public void SetTitle(string? item) => _net.ClientSendMessage(new MsgStoreSetTitle { Title = item ?? string.Empty });

    public void Request() => _net.ClientSendMessage(new MsgStoreRequest());
}

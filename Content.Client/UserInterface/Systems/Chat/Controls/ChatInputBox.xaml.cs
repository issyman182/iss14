using Content.Client.UserInterface.Systems.Gifs;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.Input;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Configuration;

namespace Content.Client.UserInterface.Systems.Chat.Controls;

[Virtual]
public class ChatInputBox : PanelContainer
{
    public const string StyleClassChatPanel = "ChatPanel";
    public const string StyleClassChatLineEdit = "ChatLineEdit";
    public const string StyleClassChatFilterOptionButton = "ChatFilterOptionButton";

    public readonly ChannelSelectorButton ChannelSelector;
    public readonly HistoryLineEdit Input;
    public readonly ChannelFilterButton FilterButton;
    // iss14: GIFs in chat via GifSnap - opens the GIF picker; only shown when the server enables GIFs.
    public readonly Button GifButton;
    private readonly IConfigurationManager _cfg;
    protected readonly BoxContainer Container;
    protected ChatChannel ActiveChannel { get; private set; } = ChatChannel.Local;

    public ChatInputBox()
    {
        Container = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            SeparationOverride = 4
        };
        AddChild(Container);

        ChannelSelector = new ChannelSelectorButton
        {
            Name = "ChannelSelector",
            ToggleMode = true,
            StyleClasses = { ChannelSelectorItemButton.StyleClassChatSelectorOptionButton },
            MinWidth = 75
        };
        Container.AddChild(ChannelSelector);
        Input = new HistoryLineEdit
        {
            Name = "Input",
            PlaceHolder = GetChatboxInfoPlaceholder(),
            HorizontalExpand = true,
            StyleClasses = { StyleClassChatLineEdit }
        };
        Container.AddChild(Input);
        GifButton = new Button
        {
            Name = "GifButton",
            Text = Loc.GetString("gifs-chat-button"),
            ToolTip = Loc.GetString("gifs-chat-button-tooltip"),
            StyleClasses = { StyleClassChatFilterOptionButton },
        };
        GifButton.OnPressed += _ => UserInterfaceManager.GetUIController<GifPickerUIController>().Toggle();
        Container.AddChild(GifButton);
        _cfg = IoCManager.Resolve<IConfigurationManager>();
        GifButton.Visible = _cfg.GetCVar(CCVars.GifsEnabled);
        FilterButton = new ChannelFilterButton
        {
            Name = "FilterButton",
            StyleClasses = { StyleClassChatFilterOptionButton }
        };
        Container.AddChild(FilterButton);
        AddStyleClass(StyleClassChatPanel);
        ChannelSelector.OnChannelSelect += UpdateActiveChannel;
    }

    private void UpdateActiveChannel(ChatSelectChannel selectedChannel)
    {
        ActiveChannel = (ChatChannel) selectedChannel;
    }

    private void OnGifsEnabledChanged(bool enabled)
    {
        GifButton.Visible = enabled;
    }

    protected override void EnteredTree()
    {
        base.EnteredTree();
        _cfg.OnValueChanged(CCVars.GifsEnabled, OnGifsEnabledChanged, true);
    }

    protected override void ExitedTree()
    {
        base.ExitedTree();
        _cfg.UnsubValueChanged(CCVars.GifsEnabled, OnGifsEnabledChanged);
    }

    private static string GetChatboxInfoPlaceholder()
    {
        return (BoundKeyHelper.IsBound(ContentKeyFunctions.FocusChat),
                BoundKeyHelper.IsBound(ContentKeyFunctions.CycleChatChannelForward)) switch
            {
                (true, true) => Loc.GetString("hud-chatbox-info",
                    ("talk-key", BoundKeyHelper.ShortKeyName(ContentKeyFunctions.FocusChat)),
                    ("cycle-key", BoundKeyHelper.ShortKeyName(ContentKeyFunctions.CycleChatChannelForward))),
                (true, false) => Loc.GetString("hud-chatbox-info-talk",
                    ("talk-key", BoundKeyHelper.ShortKeyName(ContentKeyFunctions.FocusChat))),
                (false, true) => Loc.GetString("hud-chatbox-info-cycle",
                    ("cycle-key", BoundKeyHelper.ShortKeyName(ContentKeyFunctions.CycleChatChannelForward))),
                (false, false) => Loc.GetString("hud-chatbox-info-unbound")
            };
    }
}

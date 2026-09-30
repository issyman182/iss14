using System.Linq;
using System.Text;
using Content.Client.ContextMenu.UI;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Input;
using Robust.Shared.Utility;

namespace Content.Client.DebugConsole;

/// <summary>
///     iss14: lets players get text out of the engine's debug console (F3 / ~).
///     The console lives in RobustToolbox, which we don't modify, so this hooks the existing
///     <see cref="OutputPanel"/> from the outside: right-clicking the output opens a small menu
///     with copy-to-clipboard options, and <c>copycon</c> does the same from the command bar.
/// </summary>
public sealed partial class DebugConsoleCopyManager
{
    [Dependency] private IUserInterfaceManager _ui = default!;
    [Dependency] private IClipboardManager _clipboard = default!;
    [Dependency] private ILocalizationManager _loc = default!;

    private OutputPanel? _output;
    private Popup? _popup;

    public void Initialize()
    {
        // The engine console is a DropDownDebugConsole added directly under the root control.
        var console = _ui.RootControl.Children.OfType<DropDownDebugConsole>().FirstOrDefault();
        _output = console == null ? null : FindOutputPanel(console);
        if (_output == null)
            return;

        _output.OnKeyBindDown += OnOutputKeyBindDown;
    }

    private static OutputPanel? FindOutputPanel(Control root)
    {
        foreach (var child in root.Children)
        {
            if (child is OutputPanel panel)
                return panel;

            if (FindOutputPanel(child) is { } found)
                return found;
        }

        return null;
    }

    private void OnOutputKeyBindDown(GUIBoundKeyEventArgs args)
    {
        if (args.Function != EngineKeyFunctions.UIRightClick)
            return;

        args.Handle();
        OpenMenu();
    }

    private void OpenMenu()
    {
        _popup?.Dispose();

        var popup = new Popup();
        var panel = new PanelContainer();
        panel.SetOnlyStyleClass(ContextMenuPopup.StyleClassContextMenuPopup);
        var box = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical };
        panel.AddChild(box);
        popup.AddChild(panel);

        AddEntry(box, popup, _loc.GetString("debug-console-copy-last-line"), () => CopyLines(1));
        AddEntry(box, popup, _loc.GetString("debug-console-copy-last-lines", ("count", 25)), () => CopyLines(25));
        AddEntry(box, popup, _loc.GetString("debug-console-copy-all"), () => CopyLines(int.MaxValue));

        _ui.ModalRoot.AddChild(popup);
        popup.OpenAtMouse();
        _popup = popup;
    }

    private static void AddEntry(BoxContainer box, Popup popup, string text, Action action)
    {
        var element = new ContextMenuElement(text);
        element.OnPressed += _ =>
        {
            action();
            popup.Close();
        };
        box.AddChild(element);
    }

    /// <summary>
    ///     Copies the last <paramref name="count"/> lines of console output to the clipboard.
    /// </summary>
    /// <returns>The number of lines actually copied.</returns>
    public int CopyLines(int count)
    {
        var text = GetText(count, out var copied);
        if (copied > 0)
            _clipboard.SetText(text);
        return copied;
    }

    /// <summary>
    ///     Returns the last <paramref name="count"/> lines of console output as plain text.
    /// </summary>
    public string GetText(int count, out int lines)
    {
        lines = 0;
        if (_output == null)
            return string.Empty;

        var total = _output.EntryCount;
        var start = Math.Max(0, total - Math.Max(0, count));
        var sb = new StringBuilder();
        for (var i = start; i < total; i++)
        {
            sb.AppendLine(_output.GetMessage(i).ToString());
            lines++;
        }

        return sb.ToString();
    }
}

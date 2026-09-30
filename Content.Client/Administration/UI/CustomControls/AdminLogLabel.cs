using System;
using System.Collections.Generic;
using Content.Client.UserInterface.Controls;
using Content.Shared.Administration.Logs;
using Content.Shared.Database;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Utility;

namespace Content.Client.Administration.UI.CustomControls;

/// <summary>
/// One admin log line. A <see cref="PanelContainer"/> wrapping a <see cref="RichTextLabel"/> so the line can
/// optionally be colored (toggled by the "Colored" checkbox):
/// <list type="bullet">
/// <item>line background = the log's <b>category</b> (a grouping of <see cref="LogType"/>) as a subtle tint;</item>
/// <item>the <c>HH:mm:ss [LogType]:</c> prefix = the category color (full brightness);</item>
/// <item>player usernames = gold, character names = light blue (best-effort, parsed from the message).</item>
/// </list>
/// </summary>
public sealed class AdminLogLabel : PanelContainer
{
    private static readonly Color UsernameColor = Color.FromHex("#FFD966"); // gold
    private static readonly Color CharNameColor = Color.FromHex("#8FD0FF"); // light blue

    private readonly RichTextLabel _label = new();
    private readonly StyleBoxFlat _styleBox = new();
    private readonly List<string> _playerNames;
    private bool _colored;

    public AdminLogLabel(ref SharedAdminLog log, Separator separator, List<string> playerNames)
    {
        Log = log;
        Separator = separator;
        _playerNames = playerNames;

        _label.HorizontalExpand = true;
        AddChild(_label);

        _styleBox.BackgroundColor = Color.Transparent;
        PanelOverride = _styleBox;

        Apply();
        OnVisibilityChanged += VisibilityChanged;
    }

    // 'new' intentionally shadows Control.Log (the base sawmill, unused here).
    public new SharedAdminLog Log { get; }

    public Separator Separator { get; }

    public void SetColored(bool colored)
    {
        if (_colored == colored)
            return;

        _colored = colored;
        Apply();
    }

    private void Apply()
    {
        var category = Categorize(Log.Type);
        var categoryColor = CategoryColor(category);

        _styleBox.BackgroundColor = _colored ? categoryColor.WithAlpha(0.16f) : Color.Transparent;
        // Re-assign so the panel picks up the new background color immediately.
        PanelOverride = _styleBox;

        var msg = new FormattedMessage();
        var localTime = Log.Date.ToLocalTime();

        if (_colored)
        {
            msg.PushColor(categoryColor);
            msg.AddText($"{localTime:HH:mm:ss} [{Log.Type}]: ");
            msg.Pop();

            AppendBody(msg, Log.Message);
        }
        else
        {
            msg.AddText($"{localTime:HH:mm:ss}: {Log.Message}");
        }

        _label.SetMessage(msg);
    }

    /// <summary>
    /// Appends the message, coloring each involved player's username (gold) and the character name that
    /// precedes it in an entity reference of the form <c>Character Name (uid/nNuid, ..., Username)</c> (light blue).
    /// </summary>
    private void AppendBody(FormattedMessage msg, string message)
    {
        // Collect non-overlapping colored spans, then emit the message applying them.
        var spans = new List<(int Start, int End, Color Color)>();

        foreach (var user in _playerNames)
        {
            if (string.IsNullOrEmpty(user))
                continue;

            var from = 0;
            while (true)
            {
                var u = message.IndexOf(user, from, StringComparison.Ordinal);
                if (u < 0)
                    break;

                from = u + user.Length;

                // The username itself.
                AddSpan(spans, u, u + user.Length, UsernameColor);

                // The character name sits just before the '(' that opens this entity reference.
                var open = message.LastIndexOf('(', u);
                if (open <= 0)
                    continue;

                var nameEnd = open - 1;
                while (nameEnd >= 0 && message[nameEnd] == ' ')
                    nameEnd--;

                if (nameEnd < 0)
                    continue;

                // Walk back over up to two whitespace-separated words of name characters.
                var i = nameEnd;
                var spaces = 0;
                while (i >= 0)
                {
                    var c = message[i];
                    if (IsNameChar(c))
                    {
                        i--;
                        continue;
                    }

                    if (c == ' ' && spaces < 1)
                    {
                        spaces++;
                        i--;
                        continue;
                    }

                    break;
                }

                var nameStart = i + 1;
                while (nameStart < nameEnd && message[nameStart] == ' ')
                    nameStart++;

                if (nameEnd >= nameStart)
                    AddSpan(spans, nameStart, nameEnd + 1, CharNameColor);
            }
        }

        EmitWithSpans(msg, message, spans);
    }

    private static void AddSpan(List<(int Start, int End, Color Color)> spans, int start, int end, Color color)
    {
        spans.Add((start, end, color));
    }

    private static void EmitWithSpans(FormattedMessage msg, string message, List<(int Start, int End, Color Color)> spans)
    {
        if (spans.Count == 0)
        {
            msg.AddText(message);
            return;
        }

        spans.Sort((a, b) => a.Start.CompareTo(b.Start));

        var pos = 0;
        foreach (var (start, end, color) in spans)
        {
            if (start < pos) // overlaps an already-emitted span
                continue;

            if (start > pos)
                msg.AddText(message[pos..start]);

            msg.PushColor(color);
            msg.AddText(message[start..end]);
            msg.Pop();

            pos = end;
        }

        if (pos < message.Length)
            msg.AddText(message[pos..]);
    }

    private static bool IsNameChar(char c)
    {
        return char.IsLetterOrDigit(c) || c is '\'' or '-' or '.';
    }

    private enum LogCategory
    {
        Combat,
        Healing,
        Theft,
        Chat,
        AntagAdmin,
        Engineering,
        Other,
    }

    private static LogCategory Categorize(LogType type)
    {
        switch (type)
        {
            case LogType.Damaged:
            case LogType.MeleeHit:
            case LogType.BulletHit:
            case LogType.HitScanHit:
            case LogType.Gib:
            case LogType.Explosion:
            case LogType.ExplosionHit:
            case LogType.ExplosiveDepressurization:
            case LogType.AttackArmedClick:
            case LogType.AttackArmedWide:
            case LogType.AttackUnarmedClick:
            case LogType.AttackUnarmedWide:
            case LogType.DisarmedAction:
            case LogType.DisarmedKnockdown:
            case LogType.Slip:
            case LogType.Stamina:
            case LogType.Barotrauma:
            case LogType.Asphyxiation:
            case LogType.Temperature:
            case LogType.Electrocution:
            case LogType.Radiation:
            case LogType.Flammable:
            case LogType.Trigger:
            case LogType.ThrowHit:
            case LogType.ShuttleImpact:
                return LogCategory.Combat;

            case LogType.Healed:
                return LogCategory.Healing;

            case LogType.Stripping:
            case LogType.Pickup:
            case LogType.Drop:
            case LogType.Throw:
            case LogType.Storage:
            case LogType.ForceFeed:
            case LogType.Ingestion:
            case LogType.StorePurchase:
            case LogType.StoreRefund:
            case LogType.Emag:
                return LogCategory.Theft;

            case LogType.Chat:
            case LogType.AdminMessage:
            case LogType.ChatRateLimited:
                return LogCategory.Chat;

            case LogType.AntagSelection:
            case LogType.Mind:
            case LogType.AdminCommands:
            case LogType.Respawn:
            case LogType.Vote:
            case LogType.GhostRoleTaken:
            case LogType.GhostWarp:
                return LogCategory.AntagAdmin;

            case LogType.AtmosPressureChanged:
            case LogType.AtmosPowerChanged:
            case LogType.AtmosVolumeChanged:
            case LogType.AtmosFilterChanged:
            case LogType.AtmosRatioChanged:
            case LogType.AtmosTemperatureChanged:
            case LogType.AtmosDeviceSetting:
            case LogType.RCD:
            case LogType.Construction:
            case LogType.Anchor:
            case LogType.Unanchor:
            case LogType.CableCut:
            case LogType.Tile:
            case LogType.LatticeCut:
            case LogType.WireHacking:
            case LogType.DeviceLinking:
            case LogType.DeviceNetwork:
            case LogType.FieldGeneration:
                return LogCategory.Engineering;

            default:
                return LogCategory.Other;
        }
    }

    private static Color CategoryColor(LogCategory category)
    {
        return category switch
        {
            LogCategory.Combat => Color.FromHex("#FF6B6B"), // red
            LogCategory.Healing => Color.FromHex("#6BD96B"), // green
            LogCategory.Theft => Color.FromHex("#F0A33B"), // orange
            LogCategory.Chat => Color.FromHex("#6BAEF0"), // blue
            LogCategory.AntagAdmin => Color.FromHex("#C98FF0"), // purple
            LogCategory.Engineering => Color.FromHex("#5FD0D0"), // cyan
            _ => Color.FromHex("#B0B0B0"), // gray
        };
    }

    private void VisibilityChanged(Control control)
    {
        Separator.Visible = Visible;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        OnVisibilityChanged -= VisibilityChanged;
    }
}

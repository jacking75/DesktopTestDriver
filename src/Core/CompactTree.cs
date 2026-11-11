using System.Text;

namespace DesktopTestDriver;

/// <summary>Indented one-line-per-node tree: <c>- ControlType #AutomationId "Name" [e12 t=3fa9c1d20b7e] attrs</c>.
/// Only non-default attributes are written.</summary>
public static class CompactTree
{
    public const int MaxValueCharacters = 200;
    public static string Format(IReadOnlyList<ObservationNode> nodes, bool includeBounds)
    {
        var depth = new Dictionary<string, int>(StringComparer.Ordinal); var text = new StringBuilder();
        foreach (var n in nodes)
        {
            var d = n.ParentId != null && depth.TryGetValue(n.ParentId, out var parentDepth) ? parentDepth + 1 : 0;
            depth[n.ElementId] = d;
            text.Append(' ', d * 2).Append(Line(n, includeBounds)).Append('\n');
        }
        return text.ToString();
    }
    public static string Line(ObservationNode n, bool includeBounds)
    {
        var line = new StringBuilder("- ").Append(n.ControlType);
        if (n.AutomationId.Length > 0) line.Append(" #").Append(n.AutomationId);
        if (n.Name.Length > 0) line.Append(" \"").Append(Escape(Clip(n.Name))).Append('"');
        line.Append(" [").Append(n.ElementId).Append(" t=").Append(n.SnapshotToken).Append(']');
        if (!n.Enabled) line.Append(" disabled");
        if (n.Offscreen) line.Append(" offscreen");
        if (n.Value.Available) line.Append(" value=\"").Append(Escape(Clip(n.Value.Text ?? ""))).Append('"');
        else if (n.Value.Reason == "password") line.Append(" password");
        if (n.ToggleState != null) line.Append(" toggle=").Append(n.ToggleState);
        if (n.Selected == true) line.Append(" selected");
        if (n.ExpandState is "expanded" or "collapsed" or "partially") line.Append(" expanded=").Append(n.ExpandState);
        if (n.Focused == true) line.Append(" focused");
        if (n.RangeValue is { } r) line.Append(" range=").Append(Number(r.Value)).Append('[').Append(Number(r.Minimum)).Append("..").Append(Number(r.Maximum)).Append(']');
        var patterns = n.Patterns.Where(p => p is not ("LegacyIAccessible" or "Window" or "Text" or "Selection")).ToArray();
        if (patterns.Length > 0) line.Append(" patterns=").Append(string.Join(',', patterns));
        if (includeBounds && n.Bounds is { } b) line.Append(" box=").Append(b.X).Append(',').Append(b.Y).Append(',').Append(b.Width).Append(',').Append(b.Height);
        return line.ToString();
    }
    private static string Number(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    private static string Clip(string value) => value.Length <= MaxValueCharacters ? value : value[..MaxValueCharacters] + "…(+" + (value.Length - MaxValueCharacters) + ")";
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
}

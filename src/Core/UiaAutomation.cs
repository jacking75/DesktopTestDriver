using System.Diagnostics;
using System.Text;
using System.Windows.Automation;

namespace DesktopTestDriver;

public interface IAutomationDesktop
{
    UiaResult Automation(UiaCommand command, CancellationToken cancellation);
    void ReleaseAutomation(string session) { }
}

public static class UiaAutomation
{
    private static readonly Dictionary<string, (AutomationElement Element, ObservationNode Node, string ParentToken)> virtualItems = new();
    private static readonly (AutomationPattern Pattern, string Name)[] Patterns = [
        (InvokePattern.Pattern,"Invoke"), (ValuePattern.Pattern,"Value"), (SelectionItemPattern.Pattern,"SelectionItem"),
        (TogglePattern.Pattern,"Toggle"), (ExpandCollapsePattern.Pattern,"ExpandCollapse"), (ScrollPattern.Pattern,"Scroll"),
        (ScrollItemPattern.Pattern,"ScrollItem"), (VirtualizedItemPattern.Pattern,"VirtualizedItem"),
        (ItemContainerPattern.Pattern,"ItemContainer"), (GridPattern.Pattern,"Grid"), (TablePattern.Pattern,"Table"), (RangeValuePattern.Pattern,"RangeValue")
    ];
    public static UiaResult Execute(UiaCommand command)
    {
        using var current = Process.GetCurrentProcess();
        using var desktop = new WindowsDesktop(current.SessionId, Environment.ProcessPath!, Path.GetTempPath());
        var actual = desktop.Identify(command.Window.Handle);
        if (!desktop.Valid || actual.Handle != command.Window.Handle || actual.Process != command.Window.Process) throw new GuiException("WindowChanged");
        var root = AutomationElement.FromHandle(new IntPtr(actual.Handle));
        var walker = command.TreeView switch { "raw" => TreeWalker.RawViewWalker, "content" => TreeWalker.ContentViewWalker, "control" => TreeWalker.ControlViewWalker, _ => throw new GuiException("InvalidTreeView") };
        var nodes = new List<ObservationNode>(); var refs = new Dictionary<string, AutomationElement>();
        var queue = new Queue<(AutomationElement Element, string? Parent, int Depth)>(); queue.Enqueue((root, null, 0));
        var errors = new List<string>(); string? truncation = null; int valueCharacters=0;
        while (queue.Count > 0 && nodes.Count < 1024)
        {
            var (element, parent, depth) = queue.Dequeue();
            try
            {
                var c = element.Current; var id = string.Join("-", element.GetRuntimeId());
                var patterns = Patterns.Where(p => element.TryGetCurrentPattern(p.Pattern, out _)).Select(p => p.Name).ToArray();
                ElementValue value = new(false, null, "unavailable");
                if (c.IsPassword) value = new(false, null, "redacted");
                else if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var v)) value = new(true, ((ValuePattern)v).Current.Value, null);
                else if (element.TryGetCurrentPattern(TextPattern.Pattern, out var t)) value = new(true, ((TextPattern)t).DocumentRange.GetText(65537), null);
                if (value.Text?.Length > 65536) value = new(false, null, "tooLarge");
                if(value.Text!=null) { if(valueCharacters+value.Text.Length>65536) value=new(false,null,"tooLarge"); else valueCharacters+=value.Text.Length; }
                var type = c.ControlType.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal);
                var name = c.IsPassword ? "[password]" : c.Name;
                var token = GuiProtocol.Hash(Encoding.UTF8.GetBytes(GuiProtocol.Encode(new { actual.Process, command.Window.Generation, actual.Handle, id, parent, parentToken = nodes.FirstOrDefault(n => n.ElementId == parent)?.SnapshotToken, c.AutomationId, name, type, c.ClassName, c.FrameworkId })));
                var toggle = element.TryGetCurrentPattern(TogglePattern.Pattern, out var tp) ? ((TogglePattern)tp).Current.ToggleState.ToString().ToLowerInvariant() : null;
                nodes.Add(new(id, parent, c.AutomationId, name, type, c.ClassName, c.FrameworkId, c.IsEnabled, c.IsOffscreen, value, patterns, token, toggle)); refs[id] = element;
                var child = walker.GetFirstChild(element);
                if (depth >= 16 && child != null) { truncation = "depth-limit"; continue; }
                int siblings = 0;
                while (child != null)
                {
                    if (++siblings + queue.Count + nodes.Count > 2048) { truncation = "node-limit"; break; }
                    queue.Enqueue((child, id, depth + 1)); child = walker.GetNextSibling(child);
                }
            }
            catch (Exception e) when (e is ElementNotAvailableException or System.Runtime.InteropServices.COMException or InvalidOperationException)
            { errors.Add("ProviderReadFailed"); }
        }
        if (queue.Count > 0) truncation = "node-limit";
        var observation = new Observation(nodes.FirstOrDefault()?.ElementId ?? "", nodes.ToArray(), DateTimeOffset.UtcNow, command.TreeView,
            truncation == null && errors.Count == 0 && !nodes.Any(n => n.Patterns.Contains("VirtualizedItem") || n.Patterns.Contains("ItemContainer")), truncation ?? (nodes.Any(n => n.Patterns.Contains("VirtualizedItem") || n.Patterns.Contains("ItemContainer")) ? "virtualized-items-not-enumerated" : null), errors.Distinct().ToArray(), nodes.Any(n => n.Patterns.Contains("ItemContainer") || n.Patterns.Contains("VirtualizedItem")) ? "partial-or-virtualized" : "unknown");
        ObservationNode? target = null;
        if (command.Selector != null) target = ElementQuery.Find(observation, command.Selector, command.Operation == "wait-condition");
        if (command.RuntimeId != null && virtualItems.TryGetValue(command.RuntimeId, out var savedItem))
        {
            if (savedItem.Node.SnapshotToken != command.SnapshotToken || !nodes.Any(n => n.SnapshotToken == savedItem.ParentToken)) throw new GuiException("StaleElement");
            target = savedItem.Node; refs[target.ElementId] = savedItem.Element; nodes.Add(target);
            observation = observation with { Nodes = nodes.ToArray() };
        }
        else if (command.RuntimeId != null)
        {
            target = nodes.SingleOrDefault(n => n.ElementId == command.RuntimeId) ?? throw new GuiException("StaleElement");
            if (target.SnapshotToken != command.SnapshotToken) throw new GuiException("StaleElement");
        }
        if (command.Operation == "find-item")
        {
            if (target == null || command.ItemName == null) throw new GuiException("InvalidItemQuery");
            if (!refs[target.ElementId].TryGetCurrentPattern(ItemContainerPattern.Pattern, out var pattern)) throw new GuiException("PatternNotSupported");
            var container = (ItemContainerPattern)pattern;
            var item = container.FindItemByProperty(null, AutomationElement.NameProperty, command.ItemName);
            if (item == null) throw new GuiException("ElementNotFound");
            if (container.FindItemByProperty(item, AutomationElement.NameProperty, command.ItemName) != null) throw new GuiException("AmbiguousTarget");
            var id = "virtual-" + Guid.NewGuid().ToString("N");
            var token = GuiProtocol.Hash(Encoding.UTF8.GetBytes(command.Window.Process.ImageHash + target.SnapshotToken + id));
            var supported = Patterns.Where(p => item.TryGetCurrentPattern(p.Pattern, out _)).Select(p => p.Name).ToArray();
            var found = new ObservationNode(id, target.ElementId, "", command.ItemName, "ListItem", "", "", true, true, new(false, null, "unavailable"), supported, token);
            if (virtualItems.Count >= 1024) throw new GuiException("VirtualItemBudgetExceeded");
            virtualItems[id] = (item, found, target.SnapshotToken);
            return new(observation with { Nodes = observation.Nodes.Append(found).ToArray() }, found);
        }
        if (AutomationOperations.ReadOnly(command.Operation))
        {
            return new(observation, target);
        }
        if (target == null) throw new GuiException("ElementNotFound");
        if (!target.Enabled) throw new GuiException("ElementDisabled");
        var node = refs[target.ElementId];
        T Pattern<T>(AutomationPattern pattern) => node.TryGetCurrentPattern(pattern, out var p) ? (T)p : throw new GuiException("PatternNotSupported");
        // Identity is checked again immediately before provider dispatch.
        if (!WindowRegistry.Same(actual, desktop.Identify(actual.Handle))) throw new GuiException("WindowChanged");
        switch (command.Operation)
        {
            case "invoke": NativeAutomation.Invoke(actual.Handle,node.GetRuntimeId()); break;
            case "set-value":
                if (node.Current.IsPassword) throw new GuiException("PasswordTargetDenied");
                var value = Pattern<ValuePattern>(ValuePattern.Pattern); if (value.Current.IsReadOnly) throw new GuiException("ValueReadOnly"); NativeAutomation.SetValue(actual.Handle, node.GetRuntimeId(), command.Value!); break;
            case "select-item": NativeAutomation.Select(actual.Handle,node.GetRuntimeId()); break;
            case "set-toggle": NativeAutomation.Toggle(actual.Handle,node.GetRuntimeId(),command.DesiredState=="on");break;
            case "set-expanded": NativeAutomation.Expand(actual.Handle,node.GetRuntimeId(),command.Expanded==true);break;
            case "realize-item": Pattern<VirtualizedItemPattern>(VirtualizedItemPattern.Pattern).Realize(); break;
            case "scroll-item-into-view": Pattern<ScrollItemPattern>(ScrollItemPattern.Pattern).ScrollIntoView(); break;
            case "scroll-container": NativeAutomation.Scroll(actual.Handle,node.GetRuntimeId(),command.Horizontal!.Value,command.Vertical!.Value); break;
            default: throw new GuiException("UnsupportedOperation");
        }
        return new(observation, target, "dispatched");
    }
}

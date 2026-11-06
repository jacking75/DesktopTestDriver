using System.Collections.Concurrent;

namespace DesktopTestDriver;

public interface IGuiDesktop
{
    DesktopWindow[] Windows();
    DesktopWindow[] ScopedWindows(DesktopProcess[] scope) => Windows().Where(w => scope.Contains(w.Process)).ToArray();
    DesktopWindow Identify(long handle);
    DesktopElement[] Controls(DesktopWindow window, CancellationToken cancellation);
    byte[] Capture(DesktopWindow window, CancellationToken cancellation);
    bool Focus(DesktopWindow window);
    bool PointerMatches(DesktopWindow window) => true;
    bool Valid { get; }
    void Close(DesktopWindow window);
}
public sealed class WindowRegistry(IGuiDesktop desktop)
{
    private readonly ConcurrentDictionary<string, DesktopWindow> windows = new();
    private readonly ConcurrentDictionary<string, (string Window, DesktopElement Element)> elements = new();
    public PublicWindow[] List(DesktopProcess[]? scope = null)
    {
        var selected = (scope == null ? desktop.Windows() : desktop.ScopedWindows(scope)).ToArray();
        var ids = selected.ToDictionary(w => w.Handle, _ => "win-" + Guid.NewGuid().ToString("N"));
        return selected.Select(w =>
        {
            var id = ids[w.Handle]; windows[id] = w;
            return new PublicWindow(id, w.Title, w.Process.Pid, w.Process.CreatedAt, w.Process.ImagePath, w.Bounds, w.Foreground) { ClassName = w.ClassName, OwnerWindowId = ids.GetValueOrDefault(w.OwnerHandle) };
        }).ToArray();
    }
    public static bool Same(DesktopWindow a, DesktopWindow b) => a.Handle == b.Handle && a.Process == b.Process && a.Generation == b.Generation;
    public void Retire(string id)
    {
        windows.TryRemove(id,out _);
        foreach(var element in elements.Where(e=>e.Value.Window==id).ToArray())elements.TryRemove(element.Key,out _);
    }
    public DesktopWindow Resolve(string id)
    {
        if (!windows.TryGetValue(id, out var before)) throw new GuiException("WindowNotFound");
        DesktopWindow current;
        try { current = desktop.Identify(before.Handle); } catch (GuiException) { throw; } catch { throw new GuiException("WindowNotFound"); }
        if (!Same(before, current)) throw new GuiException("WindowChanged");
        return current;
    }
    public PublicElement[] Controls(string id, CancellationToken cancellation)
    {
        var w = Resolve(id); var controls = desktop.Controls(w, cancellation);
        if (!Same(w, Resolve(id))) throw new GuiException("WindowChanged");
        return controls.Select(e =>
        {
            var elementId = "element-" + Guid.NewGuid().ToString("N"); elements[elementId] = (id, e);
            return new PublicElement(elementId, e.AutomationId, e.Name, e.ControlType, e.Bounds, e.Enabled, e.Value);
        }).ToArray();
    }
    public DesktopElement Element(string windowId, string? elementId, string? automationId, CancellationToken cancellation)
    {
        var w = Resolve(windowId); var current = desktop.Controls(w, cancellation);
        DesktopElement[] matches;
        if (elementId != null)
        {
            if (!elements.TryGetValue(elementId, out var saved) || saved.Window != windowId) throw new GuiException("ElementNotFound");
            matches = current.Where(e => e.RuntimeId == saved.Element.RuntimeId).ToArray();
            if (matches.Length == 1 && (matches[0].Bounds != saved.Element.Bounds || matches[0].AutomationId != saved.Element.AutomationId || matches[0].Name != saved.Element.Name || matches[0].ControlType != saved.Element.ControlType)) throw new GuiException("StaleElement");
        }
        else matches = current.Where(e => e.AutomationId == automationId).ToArray();
        if (matches.Length == 0) throw new GuiException("ElementNotFound");
        if (matches.Length != 1) throw new GuiException("AmbiguousTarget");
        if (!matches[0].Enabled) throw new GuiException("ElementDisabled");
        return matches[0];
    }
}

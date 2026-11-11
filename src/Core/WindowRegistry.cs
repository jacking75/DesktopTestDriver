namespace DesktopTestDriver;

/// <summary>Everything the request handler needs from the desktop. WindowsDesktop is the real one; tests use a fake.</summary>
public interface IGuiDesktop
{
    /// <summary>Visible top-level windows of the interactive session, including untitled popups.</summary>
    DesktopWindow[] Windows();
    DesktopWindow Identify(long handle);
    bool Valid { get; }
    bool PointerMatches(DesktopWindow window) => true;
    void Close(DesktopWindow window);
    void SetState(DesktopWindow window, string state);
    bool Focus(DesktopWindow window);
    byte[] Capture(DesktopWindow window, PixelRect? region, double scale, CancellationToken cancellation);
    UiaResult Automation(string session, UiaCommand command, CancellationToken cancellation);
    void StartAutomation(string session) { }
    void ReleaseAutomation(string session) { }
    string? ClipboardText() => null;
    void SetClipboardText(string text) => throw new GuiException("BackendNotSupported");
}
public static class WindowQuery
{
    public static bool Same(DesktopWindow a, DesktopWindow b) => a.Handle == b.Handle && a.Process == b.Process && a.Generation == b.Generation;
    public static string Kind(string title, string className, long owner) =>
        title.Length == 0 || className is "#32768" or "ComboLBox" or "Xaml_WindowedPopupClass" ? "popup" : owner != 0 ? "owned" : "main";
    /// <summary>Matches a window against title/class/kind/process filters given in request arguments.</summary>
    public static bool Matches(PublicWindow w, System.Text.Json.JsonElement a)
    {
        string? F(string name) => GuiProtocol.OptionalText(a, name);
        if (F("title") is { } title && w.Title != title) return false;
        if (F("titleContains") is { } contains && !w.Title.Contains(contains, StringComparison.Ordinal)) return false;
        if (F("titleRegex") is { } regex && !GuiProtocol.SafeRegex(regex).IsMatch(w.Title)) return false;
        if (F("className") is { } className && w.ClassName != className) return false;
        if (F("kind") is { } kind && w.Kind != kind) return false;
        if (F("processName") is { } process && !string.Equals(w.ProcessName, process, StringComparison.OrdinalIgnoreCase)) return false;
        if (F("ownerWindowId") is { } owner && w.OwnerWindowId != owner) return false;
        return true;
    }
    public static PublicWindow? Find(IEnumerable<PublicWindow> windows, System.Text.Json.JsonElement a, bool allowAbsent)
    {
        var matches = windows.Where(w => Matches(w, a)).Take(6).ToArray();
        if (matches.Length > 1) throw new GuiException("AmbiguousTarget", "MultipleWindowsMatch", new { matchCount = matches.Length, candidates = matches.Take(5).Select(m => new { m.WindowId, m.Title, m.ClassName, m.Kind }) }) { NoEffect = true };
        if (matches.Length == 0 && !allowAbsent) throw new GuiException("WindowNotFound") { NoEffect = true };
        return matches.SingleOrDefault();
    }
}

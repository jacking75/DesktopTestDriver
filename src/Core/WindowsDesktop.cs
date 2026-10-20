using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace DesktopTestDriver;

public sealed class WindowsDesktop(int session, string executable, string privateCaptureDirectory) : IGuiDesktop, IDisposable
{
    private readonly WindowGenerations generations = new();
    public void Dispose() => generations.Dispose();
    public static void EnableDpi() => Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
    public static string DesktopName()
    {
        var desktop = Native.OpenInputDesktop(0, false, 1); if (desktop == IntPtr.Zero) throw new GuiException("UnsupportedDesktop");
        try
        {
            var name = new StringBuilder(256); var thread = new StringBuilder(256);
            if (!Native.GetUserObjectInformation(desktop, 2, name, 512, out _) || !Native.GetUserObjectInformation(Native.GetThreadDesktop(Native.GetCurrentThreadId()), 2, thread, 512, out _) || name.ToString() != thread.ToString()) throw new GuiException("UnsupportedDesktop");
            return name.ToString();
        }
        finally { Native.CloseDesktop(desktop); }
    }
    public bool Valid { get { try { using var p = Process.GetCurrentProcess(); return p.SessionId == session && session > 0 && Native.WTSGetActiveConsoleSessionId() == session && DesktopName() == "Default" && !Elevated(p.Handle); } catch { return false; } } }
    public static bool Elevated(IntPtr process)
    {
        if (!Native.OpenProcessToken(process, 8, out var token)) throw new GuiException("AccessDenied");
        try { if (!Native.GetTokenInformation(token, 20, out int elevated, 4, out _)) throw new GuiException("AccessDenied"); return elevated != 0; } finally { Native.CloseHandle(token); }
    }
    public DesktopWindow Identify(long handle)
    {
        var h = new IntPtr(handle);
        if (!Native.IsWindow(h)) throw new GuiException("WindowNotFound", "WindowDestroyed");
        if (!Native.IsWindowVisible(h)) throw new GuiException("WindowNotFound", "WindowHidden");
        if (Native.IsIconic(h)) throw new GuiException("WindowNotFound", "WindowMinimized");
        Native.GetWindowThreadProcessId(h, out var pid); using var p = Process.GetProcessById((int)pid);
        if (p.SessionId != session || Elevated(p.Handle)) throw new GuiException("AccessDenied");
        var title = new StringBuilder(1024); Native.GetWindowText(h, title, title.Capacity);
        if (!Native.GetClientRect(h, out var r)) throw new GuiException("ObservationUnavailable"); var origin = new Native.Point();
        if (!Native.ClientToScreen(h, ref origin)) throw new GuiException("ObservationUnavailable");
        return new(handle, Processes.Identify(p), title.ToString(), new(origin.X, origin.Y, r.Right - r.Left, r.Bottom - r.Top), Native.GetDpiForWindow(h), Native.GetForegroundWindow() == h, generations.Get(handle));
    }
    public DesktopWindow[] Windows()
    {
        if (!Valid) throw new GuiException("NoInteractiveSession"); var found = new List<DesktopWindow>();
        Native.EnumWindows((h, _) => { try { if (Native.DwmGetWindowAttribute(h, 14, out var cloaked, 4) == 0 && cloaked != 0) return true; var w = Identify(h.ToInt64()); if (w.Title.Length > 0 && w.Bounds.Width > 0 && w.Bounds.Height > 0) found.Add(w); } catch { } return true; }, IntPtr.Zero);
        return found.ToArray();
    }
    public bool Focus(DesktopWindow window)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--focus", window.Handle.ToString(System.Globalization.CultureInfo.InvariantCulture), window.Process.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture), window.Process.CreatedAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(arg);
        using var helper = Process.Start(start) ?? throw new GuiException("FocusDenied");
        if (!helper.WaitForExit(1200)) { helper.Kill(); helper.WaitForExit(500); return false; }
        return helper.ExitCode == 0 && Native.GetForegroundWindow().ToInt64() == window.Handle;
    }
    public bool PointerMatches(DesktopWindow window) => Native.GetCursorPos(out var point) && Native.GetAncestor(Native.WindowFromPoint(point), 2).ToInt64() == window.Handle;
    public static bool ActivateWindow(long handle, int expectedPid, long expectedStart)
    {
        var h = new IntPtr(handle); Native.GetWindowThreadProcessId(h, out var pid); if (pid != expectedPid) return false;
        using var process = Process.GetProcessById((int)pid); using var current = Process.GetCurrentProcess();
        if (process.StartTime.ToUniversalTime().Ticks != expectedStart || process.SessionId != current.SessionId || DesktopName() != "Default" || Elevated(process.Handle)) return false;
        _ = Native.PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        var thread = Native.GetCurrentThreadId(); var foregroundThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _); var targetThread = Native.GetWindowThreadProcessId(h, out _);
        bool foregroundAttached = false, targetAttached = false;
        try
        {
            if (foregroundThread != 0 && foregroundThread != thread) foregroundAttached = Native.AttachThreadInput(thread, foregroundThread, true);
            if (targetThread != 0 && targetThread != thread && targetThread != foregroundThread) targetAttached = Native.AttachThreadInput(thread, targetThread, true);
            _ = Native.BringWindowToTop(h); _ = Native.SetForegroundWindow(h); return Native.GetForegroundWindow() == h;
        }
        finally { if (targetAttached) Native.AttachThreadInput(thread, targetThread, false); if (foregroundAttached) Native.AttachThreadInput(thread, foregroundThread, false); }
    }
    public void Close(DesktopWindow window) { if (!Native.PostMessage(new IntPtr(window.Handle), 0x10, IntPtr.Zero, IntPtr.Zero)) throw new GuiException("CloseWindowFailed"); }
    private void Visible(DesktopWindow w)
    {
        var b = w.Bounds; if (b.Width <= 0 || b.Height <= 0 || (long)b.Width * b.Height > 16000000) throw new GuiException("ObservationUnavailable");
        // Require complete monitor coverage, including gaps in nonrectangular monitor layouts.
        var uncovered = new List<PixelRect> { b };
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr d, ref Native.NativeRect r, IntPtr x) =>
        {
            var monitor = new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top); var next = new List<PixelRect>();
            foreach (var region in uncovered)
            {
                if (!region.Intersects(monitor)) { next.Add(region); continue; }
                int l = Math.Max(region.X, monitor.X), t = Math.Max(region.Y, monitor.Y), right = Math.Min(region.X + region.Width, monitor.X + monitor.Width), bottom = Math.Min(region.Y + region.Height, monitor.Y + monitor.Height);
                if (region.Y < t) next.Add(new(region.X, region.Y, region.Width, t - region.Y)); if (bottom < region.Y + region.Height) next.Add(new(region.X, bottom, region.Width, region.Y + region.Height - bottom));
                if (region.X < l) next.Add(new(region.X, t, l - region.X, bottom - t)); if (right < region.X + region.Width) next.Add(new(right, t, region.X + region.Width - right, bottom - t));
            }
            uncovered = next; return true;
        }, IntPtr.Zero);
        if (uncovered.Count != 0) throw new GuiException("ObservationUnavailable", "ClientOutsideMonitors");
        bool covered = false, seen = false; string obstruction = "";
        Native.EnumWindows((h, _) =>
        {
            if (h.ToInt64() == w.Handle) { seen = true; return false; }
            if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
            if (Native.DwmGetWindowAttribute(h, 14, out var cloaked, 4) == 0 && cloaked != 0) return true;
            var style = Native.GetWindowLongPtr(h, -20).ToInt64();
            // WGC captures only the target surface. Mouse-transparent layered overlays never become its pixels.
            if ((style & 0x80020) == 0x80020) return true;
            if (Native.GetLayeredWindowAttributes(h, out var colorKey, out var alpha, out var layerFlags) && (layerFlags & 2) != 0 && alpha == 0) return true;
            if (Native.GetWindowRect(h, out var r) && new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top).Intersects(b))
            {
                int regionKind = 0; var region = Native.CreateRectRgn(0, 0, 0, 0);
                try { if (region != IntPtr.Zero) { regionKind = Native.GetWindowRgn(h, region); if (regionKind == 1) return true; if (regionKind is 2 or 3) { Native.OffsetRgn(region, r.Left, r.Top); var client = new Native.NativeRect { Left = b.X, Top = b.Y, Right = b.X + b.Width, Bottom = b.Y + b.Height }; if (!Native.RectInRegion(region, ref client)) return true; } } } finally { if (region != IntPtr.Zero) Native.DeleteObject(region); }
                Native.GetWindowDisplayAffinity(h, out var affinity); covered = true; var className = new StringBuilder(256); Native.GetClassName(h, className, 256); Native.GetWindowThreadProcessId(h, out var obstructingPid); obstruction = $"ClientOccluded pid={obstructingPid} class={className} rect={r.Left},{r.Top},{r.Right},{r.Bottom} styles={Native.GetWindowLongPtr(h, -20).ToInt64():x} alpha={alpha} layerFlags={layerFlags} regionKind={regionKind} affinity={affinity:x}";
            }
            return true;
        }, IntPtr.Zero);
        if (!seen || covered) throw new GuiException("ObservationUnavailable", !seen ? "WindowNotInVisibleZOrder" : obstruction);
    }
    public byte[] Capture(DesktopWindow w, CancellationToken cancellation)
    {
        Visible(w); var store = new StateStore(privateCaptureDirectory); var path = store.PathFor("capture-helper-" + Guid.NewGuid().ToString("N") + ".png");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--capture", w.Handle.ToString(System.Globalization.CultureInfo.InvariantCulture), w.Process.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture), w.Process.CreatedAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture), path, w.Bounds.X.ToString(System.Globalization.CultureInfo.InvariantCulture), w.Bounds.Y.ToString(System.Globalization.CultureInfo.InvariantCulture), w.Bounds.Width.ToString(System.Globalization.CultureInfo.InvariantCulture), w.Bounds.Height.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(arg);
        using var helper = Process.Start(start) ?? throw new GuiException("ObservationUnavailable"); var error = helper.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(2200);
            helper.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            if (helper.ExitCode != 0) { var detail = error.GetAwaiter().GetResult(); throw new GuiException("ObservationUnavailable", "GraphicsCaptureFailed " + detail.Trim()); }
            cancellation.ThrowIfCancellationRequested(); Visible(w); var after = Identify(w.Handle);
            if (!WindowRegistry.Same(w, after) || w.Bounds != after.Bounds || w.Dpi != after.Dpi) throw new GuiException("StaleCapture");
            return File.ReadAllBytes(path);
        }
        catch (OperationCanceledException) { if (!helper.HasExited) { helper.Kill(); helper.WaitForExit(500); } throw; }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    public DesktopElement[] Controls(DesktopWindow w, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }; start.ArgumentList.Add("--uia"); start.ArgumentList.Add(w.Handle.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var process = Process.Start(start) ?? throw new GuiException("ObservationUnavailable");
        var read = process.StandardOutput.ReadToEndAsync(cancellation); var errors = process.StandardError.ReadToEndAsync(cancellation);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(3000);
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            var text = read.GetAwaiter().GetResult(); if (process.ExitCode != 0) throw new GuiException("ObservationUnavailable", "UIAutomationHelperFailed"); return GuiProtocol.Decode<DesktopElement[]>(text);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            if (!process.HasExited) { process.Kill(); process.WaitForExit(500); }
            throw new GuiException("ObservationUnavailable", "UIAutomationTimeout");
        }
        finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(500); } }
    }
    public static DesktopElement[] ReadControls(long handle)
    {
        var root = AutomationElement.FromHandle(new IntPtr(handle)); var nodes = new List<DesktopElement>(); var queue = new Queue<(AutomationElement, int)>(); queue.Enqueue((root, 0));
        var origin = new Native.Point(); if (!Native.ClientToScreen(new IntPtr(handle), ref origin)) throw new GuiException("WindowNotFound");
        while (queue.Count > 0 && nodes.Count < 1024)
        {
            var (element, depth) = queue.Dequeue(); var c = element.Current; var b = c.BoundingRectangle; ElementValue value;
            if (c.IsPassword) value = new(false, null, "Password");
            else if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var v)) value = new(true, ((ValuePattern)v).Current.Value, null);
            else if (element.TryGetCurrentPattern(TextPattern.Pattern, out var t)) value = new(true, ((TextPattern)t).DocumentRange.GetText(65537), null);
            else value = new(false, null, "NotSupported");
            if (value.Available && value.Text?.Length > 65536) value = new(false, null, "ValueTooLarge");
            nodes.Add(new(string.Join("-", element.GetRuntimeId()), c.AutomationId, c.IsPassword ? "[password]" : c.Name, c.ControlType.ProgrammaticName, b.IsEmpty ? new(0, 0, 0, 0) : new((int)Math.Round(b.X) - origin.X, (int)Math.Round(b.Y) - origin.Y, (int)Math.Round(b.Width), (int)Math.Round(b.Height)), c.IsEnabled, value));
            if (depth >= 16) continue; var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
            for (int i = 0; child != null && i < 256 && queue.Count < 1024; i++, child = TreeWalker.ControlViewWalker.GetNextSibling(child)) queue.Enqueue((child, depth + 1));
        }
        return nodes.ToArray();
    }
}

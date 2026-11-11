using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
namespace DesktopTestDriver;

public sealed class WindowsDesktop(int session, string executable, string captureDirectory) : IGuiDesktop, IDisposable
{
    private readonly WindowGenerations generations = new();
    private readonly Dictionary<string, WorkerProcess> automation = new();
    private WorkerProcess? desktopWorker;
    private readonly object desktopGate = new();
    public const int CaptureLimitMilliseconds = 2200;
    private WorkerProcess Worker(string session)
    {
        lock (automation)
        {
            if (!automation.TryGetValue(session, out var worker) || !worker.Alive) { worker?.Dispose(); automation[session] = worker = new WorkerProcess(executable); }
            return worker;
        }
    }
    public void StartAutomation(string session) { try { _ = Worker(session); } catch (GuiException) { } }
    public UiaResult Automation(string session, UiaCommand command, CancellationToken cancellation)
    {
        var worker = Worker(session);
        var reply = worker.Call(new("uia", command), GuiProtocol.MaxWaitMilliseconds, cancellation);
        if (!reply.Ok) throw new GuiException(reply.Error?.Code ?? "ProviderError", reply.Error?.Message, reply.Error?.Data) { NoEffect = !reply.DispatchStarted };
        return reply.Data!.Value.Deserialize<UiaResult>(GuiProtocol.Json)!;
    }
    public void ReleaseAutomation(string session) { lock (automation) { if (automation.Remove(session, out var worker)) worker.Dispose(); } }
    private WorkerReply DesktopCall(WorkerRequest request, int timeout, CancellationToken cancellation)
    {
        lock (desktopGate)
        {
            if (desktopWorker is not { Alive: true }) { desktopWorker?.Dispose(); desktopWorker = new WorkerProcess(executable); }
            return desktopWorker.Call(request, timeout, cancellation);
        }
    }
    public void Dispose()
    {
        lock (automation) { foreach (var worker in automation.Values) worker.Dispose(); automation.Clear(); }
        lock (desktopGate) desktopWorker?.Dispose();
        generations.Dispose();
    }
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
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQuerySessionInformation(IntPtr server, int session, int info, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr buffer);
    public static string SessionKind(int session)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, 8, out var state, out var stateBytes)) return "unavailable";
        try { if (stateBytes < 4 || Marshal.ReadInt32(state) != 0) return "disconnected"; } finally { WTSFreeMemory(state); }
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, 16, out var protocol, out var protocolBytes)) return "unavailable";
        try { return protocolBytes < 2 ? "unavailable" : Marshal.ReadInt16(protocol) switch { 0 => "console", 2 => "rdp", _ => "unsupported" }; } finally { WTSFreeMemory(protocol); }
    }
    /// <summary>The agent must run in a connected interactive session on the Default desktop. Elevation is allowed.</summary>
    public bool Valid { get { try { using var p = Process.GetCurrentProcess(); return p.SessionId == session && session > 0 && SessionKind(session) is "console" or "rdp" && DesktopName() == "Default"; } catch { return false; } } }
    public DesktopWindow Identify(long handle)
    {
        var h = new IntPtr(handle);
        if (!Native.IsWindow(h)) throw new GuiException("WindowNotFound", "WindowDestroyed") { NoEffect = true };
        if (!Native.IsWindowVisible(h)) throw new GuiException("WindowNotFound", "WindowHidden") { NoEffect = true };
        Native.GetWindowThreadProcessId(h, out var pid);
        var process = Processes.Identify((int)pid);
        var title = new StringBuilder(1024); Native.GetWindowText(h, title, title.Capacity);
        var minimized = Native.IsIconic(h);
        PixelRect bounds;
        if (minimized) bounds = new(0, 0, 0, 0);
        else
        {
            if (!Native.GetClientRect(h, out var r)) throw new GuiException("ObservationUnavailable") { NoEffect = true };
            var origin = new Native.Point(); if (!Native.ClientToScreen(h, ref origin)) throw new GuiException("ObservationUnavailable") { NoEffect = true };
            bounds = new(origin.X, origin.Y, r.Right - r.Left, r.Bottom - r.Top);
        }
        var className = new StringBuilder(256); Native.GetClassName(h, className, className.Capacity);
        var owner = Native.GetWindow(h, 4).ToInt64();
        return new(handle, process, title.ToString(), bounds, Native.GetDpiForWindow(h), Native.GetForegroundWindow() == h, generations.Get(handle))
        { ClassName = className.ToString(), OwnerHandle = owner, Minimized = minimized, Kind = WindowQuery.Kind(title.ToString(), className.ToString(), owner) };
    }
    public DesktopWindow[] Windows()
    {
        if (!Valid) throw new GuiException("NoInteractiveSession") { NoEffect = true };
        var found = new List<DesktopWindow>();
        Native.EnumWindows((h, _) =>
        {
            try
            {
                if (!Native.IsWindowVisible(h)) return true;
                if (Native.DwmGetWindowAttribute(h, 14, out var cloaked, 4) == 0 && cloaked != 0) return true;
                if (!Native.GetWindowRect(h, out var r) || r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0) { if (!Native.IsIconic(h)) return true; }
                found.Add(Identify(h.ToInt64()));
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return found.ToArray();
    }
    public bool Focus(DesktopWindow window)
    {
        var reply = DesktopCall(new("focus", Focus: new(window.Handle, window.Process.Pid)), 1500, CancellationToken.None);
        return reply.Ok && reply.Data?.GetBoolean() == true && Native.GetForegroundWindow().ToInt64() == window.Handle;
    }
    public bool PointerMatches(DesktopWindow window) => Native.GetCursorPos(out var point) && Native.GetAncestor(Native.WindowFromPoint(point), 2).ToInt64() == window.Handle;
    /// <summary>Runs inside the desktop worker. Attaching input queues lets a background process bring a window forward.</summary>
    public static bool ActivateWindow(long handle, int expectedPid)
    {
        var h = new IntPtr(handle); Native.GetWindowThreadProcessId(h, out var pid); if (pid != expectedPid) return false;
        if (Native.IsIconic(h)) Native.ShowWindowAsync(h, 9);
        _ = Native.PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        var thread = Native.GetCurrentThreadId(); var foregroundThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _); var targetThread = Native.GetWindowThreadProcessId(h, out _);
        bool foregroundAttached = false, targetAttached = false;
        try
        {
            if (foregroundThread != 0 && foregroundThread != thread) foregroundAttached = Native.AttachThreadInput(thread, foregroundThread, true);
            if (targetThread != 0 && targetThread != thread && targetThread != foregroundThread) targetAttached = Native.AttachThreadInput(thread, targetThread, true);
            _ = Native.BringWindowToTop(h); _ = Native.SetForegroundWindow(h);
            for (int i = 0; i < 20 && Native.GetForegroundWindow() != h; i++) Thread.Sleep(10);
            return Native.GetForegroundWindow() == h;
        }
        finally { if (targetAttached) Native.AttachThreadInput(thread, targetThread, false); if (foregroundAttached) Native.AttachThreadInput(thread, foregroundThread, false); }
    }
    public void Close(DesktopWindow window) { if (!Native.PostMessage(new IntPtr(window.Handle), 0x10, IntPtr.Zero, IntPtr.Zero)) throw new GuiException("CloseWindowFailed"); }
    public void SetState(DesktopWindow window, string state)
    {
        if (state == "close") { Close(window); return; }
        if (!Native.ShowWindowAsync(new IntPtr(window.Handle), state switch { "minimize" => 6, "maximize" => 3, _ => 9 }) && state != "restore") throw new GuiException("WindowStateFailed");
    }
    public byte[] Capture(DesktopWindow w, PixelRect? region, double scale, CancellationToken cancellation)
    {
        if (w.Minimized) throw new GuiException("WindowMinimized") { NoEffect = true };
        Directory.CreateDirectory(captureDirectory);
        var path = Path.Combine(captureDirectory, "capture-helper-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            var reply = DesktopCall(new("capture", Capture: new(w.Handle, w.Process.Pid, w.Process.CreatedAt.UtcTicks, w.Bounds, region, scale, path)), CaptureLimitMilliseconds, cancellation);
            if (!reply.Ok) throw new GuiException(reply.Error?.Code ?? "ObservationUnavailable", reply.Error?.Message);
            var after = Identify(w.Handle);
            if (!WindowQuery.Same(w, after) || w.Bounds != after.Bounds || w.Dpi != after.Dpi) throw new GuiException("StaleCapture");
            return File.ReadAllBytes(path);
        }
        finally { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } }
    }
    public string? ClipboardText() => Sta(() => System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null);
    public void SetClipboardText(string text) => Sta<object?>(() => { System.Windows.Clipboard.SetDataObject(text, true); return null; });
    private static T Sta<T>(Func<T> action)
    {
        T result = default!; Exception? error = null;
        var thread = new Thread(() => { try { result = action(); } catch (Exception e) { error = e; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(3000)) throw new GuiException("ClipboardUnavailable", "ClipboardTimeout");
        if (error != null) throw new GuiException("ClipboardUnavailable", error.Message);
        return result;
    }
}

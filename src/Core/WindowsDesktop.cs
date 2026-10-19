using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopTestDriver;

public interface IDesktop
{
    Observation Inspect();
    string Capture(WindowIdentity window, string destination);
    UiNode[] Controls(WindowIdentity window);
    bool Focus(WindowIdentity window);
    bool PhysicalConflict { get; }
}

public sealed class WindowsDesktop(RunConfig config, string clientExecutable) : IDesktop
{
    private PhysicalInputMonitor? monitor;
    public bool PhysicalConflict => monitor?.Conflict == true;
    public IDisposable MonitorInput(Func<bool>? allowExternalInjection = null) => monitor = new PhysicalInputMonitor(allowExternalInjection);
    public static void EnableDpi() => Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
    public static string DesktopName()
    {
        var desktop = Native.OpenInputDesktop(0, false, 1);
        if (desktop == IntPtr.Zero) throw new DriverException(Error.DesktopChanged);
        try
        {
            var value = new StringBuilder(256);
            if (!Native.GetUserObjectInformation(desktop, 2, value, 512, out _)) throw new DriverException(Error.DesktopChanged);
            var threadDesktop = Native.GetThreadDesktop(Native.GetCurrentThreadId());
            var threadName = new StringBuilder(256);
            if (!Native.GetUserObjectInformation(threadDesktop, 2, threadName, 512, out _) || threadName.ToString() != value.ToString())
                throw new DriverException(Error.DesktopChanged);
            return value.ToString();
        }
        finally { Native.CloseDesktop(desktop); }
    }
    public static bool ConsoleValid(int session)
    {
        using var current = Process.GetCurrentProcess();
        return current.SessionId == session && session > 0 && Native.WTSGetActiveConsoleSessionId() == session && DesktopName() == "Default" && !IsElevated(current.Handle);
    }
    public static bool IsElevated(IntPtr process)
    {
        if (!Native.OpenProcessToken(process, 8, out var token)) throw new DriverException(Error.ObservationUnavailable);
        try
        {
            if (!Native.GetTokenInformation(token, 20, out int elevated, 4, out _)) throw new DriverException(Error.ObservationUnavailable);
            return elevated != 0;
        }
        finally { Native.CloseHandle(token); }
    }
    public static WindowIdentity Identify(long hwnd)
    {
        var handle = new IntPtr(hwnd);
        if (!Native.IsWindow(handle) || !Native.IsWindowVisible(handle) || Native.IsIconic(handle)) throw new DriverException(Error.WindowChanged);
        Native.GetWindowThreadProcessId(handle, out var pid);
        using var process = Process.GetProcessById((int)pid);
        if (IsElevated(process.Handle)) throw new DriverException(Error.Unsupported);
        var identity = Processes.Identify(process);
        var title = new StringBuilder(513);
        Native.GetWindowText(handle, title, title.Capacity);
        if (!Native.GetClientRect(handle, out var rect)) throw new DriverException(Error.ObservationUnavailable);
        var point = new Native.Point();
        if (!Native.ClientToScreen(handle, ref point)) throw new DriverException(Error.ObservationUnavailable);
        return new(hwnd, (int)pid, identity.CreatedAt, identity.ImagePath, identity.ImageHash, title.ToString(),
            new(point.X, point.Y, rect.Right - rect.Left, rect.Bottom - rect.Top));
    }
    public Observation Inspect()
    {
        if (!ConsoleValid(config.TargetSession)) throw new DriverException(Error.SessionChanged);
        var windows = new List<WindowIdentity>();
        Native.EnumWindows((hwnd, _) =>
        {
            try
            {
                var title = new StringBuilder(513);
                Native.GetWindowText(hwnd, title, title.Capacity);
                if (!config.Windows.Any(w => w.Title == "*" || w.Title == title.ToString())) return true;
                var identity = Identify(hwnd.ToInt64());
                if (config.Windows.Any(w => (w.Title == "*" || w.Title == identity.Title) && string.Equals(w.ImagePath, identity.ImagePath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(w.ImageHash, identity.ImageHash, StringComparison.OrdinalIgnoreCase))) windows.Add(identity);
            }
            catch { /* Inaccessible windows are not within the observable scope. */ }
            return true;
        }, IntPtr.Zero);
        var monitors = new List<MonitorInfo>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitorHandle, IntPtr _, ref Native.NativeRect rect, IntPtr _) =>
        {
            var info = new Native.Monitor { Size = Marshal.SizeOf<Native.Monitor>(), Device = "" };
            if (!Native.GetMonitorInfo(monitorHandle, ref info)) return true;
            uint x = 96, y = 96;
            _ = Native.GetDpiForMonitor(monitorHandle, 0, out x, out y);
            monitors.Add(new(info.Device, new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top), x, y, (info.Flags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        return new(config.TargetSession, DesktopName(), Native.GetForegroundWindow().ToInt64(), windows.ToArray(), monitors.ToArray(), false);
    }
    public string Capture(WindowIdentity window, string destination)
    {
        if (Native.GetForegroundWindow().ToInt64() != window.Hwnd || Identify(window.Hwnd) != window) throw new DriverException(Error.WindowChanged);
        var b = window.Bounds;
        if (b.Width <= 0 || b.Height <= 0 || (long)b.Width * b.Height > 16_000_000) throw new DriverException(Error.ObservationUnavailable);
        var screen = Native.GetDC(IntPtr.Zero);
        var memory = Native.CreateCompatibleDC(screen);
        var bitmap = Native.CreateCompatibleBitmap(screen, b.Width, b.Height);
        if (screen == IntPtr.Zero || memory == IntPtr.Zero || bitmap == IntPtr.Zero)
        {
            if (bitmap != IntPtr.Zero) Native.DeleteObject(bitmap);
            if (memory != IntPtr.Zero) Native.DeleteDC(memory);
            if (screen != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, screen);
            throw new DriverException(Error.ObservationUnavailable);
        }
        var old = Native.SelectObject(memory, bitmap);
        try
        {
            if (!Native.BitBlt(memory, 0, 0, b.Width, b.Height, screen, b.X, b.Y, 0x40CC0020)) throw new DriverException(Error.ObservationUnavailable);
            Native.SelectObject(memory, old);
            var pixels = new byte[checked(b.Width * b.Height * 4)];
            var header = new Native.BitmapInfo { Size = 40, Width = b.Width, Height = -b.Height, Planes = 1, BitCount = 32 };
            if (Native.GetDIBits(memory, bitmap, 0, (uint)b.Height, pixels, ref header, 0) != b.Height || !pixels.Where((_, i) => i % 4 != 3).Any(v => v != 0))
                throw new DriverException(Error.ObservationUnavailable);
            if (Native.GetForegroundWindow().ToInt64() != window.Hwnd || Identify(window.Hwnd) != window) throw new DriverException(Error.WindowChanged);
            var source = BitmapSource.Create(b.Width, b.Height, 96, 96, PixelFormats.Bgr32, null, pixels, b.Width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var output = new FileStream(destination, FileMode.CreateNew);
            encoder.Save(output);
            return Wire.FileHash(destination);
        }
        finally
        {
            Native.SelectObject(memory, old);
            Native.DeleteObject(bitmap); Native.DeleteDC(memory); Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }
    public UiNode[] Controls(WindowIdentity window)
    {
        // A broken UIA provider must not block input release or the pipe indefinitely.
        var start = new ProcessStartInfo(clientExecutable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--uia"); start.ArgumentList.Add(window.Hwnd.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var process = Process.Start(start) ?? throw new DriverException(Error.ObservationUnavailable);
        var read = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(1500)) { process.Kill(); process.WaitForExit(500); throw new DriverException(Error.ObservationUnavailable); }
        if (process.ExitCode != 0 || !read.Wait(100)) throw new DriverException(Error.ObservationUnavailable);
        return Wire.Decode<UiNode[]>(read.Result);
    }
    public static UiNode[] ReadControls(long hwnd)
    {
        var root = AutomationElement.FromHandle(new IntPtr(hwnd));
        var nodes = new List<UiNode>();
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count != 0 && nodes.Count < 128)
        {
            var (element, depth) = queue.Dequeue();
            var current = element.Current; var b = current.BoundingRectangle;
            // Editable/password contents are omitted. Raw UI labels and images remain private evidence.
            var name = current.ControlType == ControlType.Edit || current.IsPassword ? "[redacted]" : current.Name;
            string? toggle = element.TryGetCurrentPattern(TogglePattern.Pattern, out var pattern) ? ((TogglePattern)pattern).Current.ToggleState.ToString() : null;
            nodes.Add(new(name[..Math.Min(name.Length, 512)], current.AutomationId, current.ControlType.ProgrammaticName,
                b.IsEmpty ? new(0, 0, 0, 0) : new((int)b.X, (int)b.Y, (int)b.Width, (int)b.Height), current.IsEnabled, toggle));
            if (depth >= 8) continue;
            var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
            for (int i = 0; child != null && i < 64 && queue.Count < 128; i++, child = TreeWalker.ControlViewWalker.GetNextSibling(child)) queue.Enqueue((child, depth + 1));
        }
        return nodes.ToArray();
    }
    public bool Focus(WindowIdentity window) => Native.SetForegroundWindow(new IntPtr(window.Hwnd)) && Native.GetForegroundWindow().ToInt64() == window.Hwnd;
}

public sealed class WindowsInput : IInputBackend
{
    internal static readonly UIntPtr Marker = InputConflictPolicy.DriverMarker;
    public bool IsDown(string kind, int code) => (Native.GetAsyncKeyState(kind == "button" ? code switch { 1 => 1, 2 => 2, 3 => 4, _ => throw new DriverException(Error.InvalidRequest) } : code) & 0x8000) != 0;
    public void Down(string kind, int code) => Send(kind, code, false);
    public void Up(string kind, int code) => Send(kind, code, true);
    private static void Send(string kind, int code, bool up)
    {
        Native.Input input;
        if (kind == "button")
        {
            uint flag = code switch { 1 => up ? 4u : 2u, 2 => up ? 16u : 8u, 3 => up ? 64u : 32u, _ => throw new DriverException(Error.InvalidRequest) };
            input = new() { Type = 0, Mouse = new() { Flags = flag, Extra = Marker } };
        }
        else input = new() { Type = 1, Keyboard = new() { Vk = kind == "unicode" ? (ushort)0 : (ushort)code,
            Scan = kind == "unicode" ? (ushort)code : (ushort)0, Flags = (up ? 2u : 0u) | (kind == "unicode" ? 4u : Extended(code)), Extra = Marker } };
        Inject(input);
    }
    private static uint Extended(int vk) => vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0xA3 or 0xA5 or 0x5B or 0x5C ? 1u : 0u;
    public void Move(int x, int y)
    {
        int left = Native.GetSystemMetrics(76), top = Native.GetSystemMetrics(77), width = Native.GetSystemMetrics(78), height = Native.GetSystemMetrics(79);
        if (width <= 1 || height <= 1 || x < left || y < top || x >= left + width || y >= top + height) throw new DriverException(Error.InvalidRequest);
        Inject(new() { Type = 0, Mouse = new() { X = (int)Math.Round((x - left) * 65535.0 / (width - 1)), Y = (int)Math.Round((y - top) * 65535.0 / (height - 1)), Flags = 0xC001, Extra = Marker } });
    }
    public void Wheel(int delta) => Inject(new() { Type = 0, Mouse = new() { Data = unchecked((uint)delta), Flags = 0x800, Extra = Marker } });
    private static void Inject(Native.Input input)
    {
        if (Native.SendInput(1, [input], Marshal.SizeOf<Native.Input>()) != 1) throw new DriverException(Error.InjectionFailed);
    }
}

public static class InputConflictPolicy
{
    public static readonly UIntPtr DriverMarker = new(0x44544431);
    public static bool IsConflict(bool injected, UIntPtr marker, bool delegatedExternalInput) =>
        !injected || marker != DriverMarker && !delegatedExternalInput;
    public static bool ExternalInputAllowed(RunStore store) =>
        !store.Stopped && store.Config.Delegation!.ProductPermissions.Contains("control") &&
        File.Exists(store.PathFor("input-sealed.json"));
}

public sealed class PhysicalInputMonitor : IDisposable
{
    private readonly Func<bool> allowExternalInjection;
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready = new();
    private uint threadId;
    private volatile bool conflict;
    private bool installed;
    public bool Conflict => conflict;
    public PhysicalInputMonitor(Func<bool>? allowExternalInjection = null)
    {
        this.allowExternalInjection = allowExternalInjection ?? (() => false);
        thread = new Thread(Pump) { IsBackground = true, Name = "DesktopTestDriver input guard" };
        thread.Start();
        if (!ready.Wait(2000) || !installed) throw new DriverException(Error.ObservationUnavailable);
    }
    private void Pump()
    {
        threadId = Native.GetCurrentThreadId();
        Native.HookCallback keyCallback = (code, message, data) =>
        {
            if (code >= 0)
            {
                var value = Marshal.PtrToStructure<Native.KeyHook>(data);
                if (InputConflictPolicy.IsConflict((value.Flags & 0x10) != 0, value.Extra, Delegated())) conflict = true;
            }
            return Native.CallNextHookEx(IntPtr.Zero, code, message, data);
        };
        Native.HookCallback mouseCallback = (code, message, data) =>
        {
            if (code >= 0)
            {
                var value = Marshal.PtrToStructure<Native.MouseHook>(data);
                if (InputConflictPolicy.IsConflict((value.Flags & 1) != 0, value.Extra, Delegated())) conflict = true;
            }
            return Native.CallNextHookEx(IntPtr.Zero, code, message, data);
        };
        var key = Native.SetWindowsHookEx(13, keyCallback, IntPtr.Zero, 0);
        var mouse = Native.SetWindowsHookEx(14, mouseCallback, IntPtr.Zero, 0);
        installed = key != IntPtr.Zero && mouse != IntPtr.Zero;
        ready.Set();
        try { if (installed) while (Native.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0) { Native.TranslateMessage(ref message); Native.DispatchMessage(ref message); } }
        finally { if (key != IntPtr.Zero) Native.UnhookWindowsHookEx(key); if (mouse != IntPtr.Zero) Native.UnhookWindowsHookEx(mouse); GC.KeepAlive(keyCallback); GC.KeepAlive(mouseCallback); }
    }
    private bool Delegated() { try { return allowExternalInjection(); } catch { return false; } }
    public void Dispose() { Native.PostThreadMessage(threadId, 0x12, UIntPtr.Zero, IntPtr.Zero); thread.Join(1000); ready.Dispose(); }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct Monitor { public int Size; public NativeRect Bounds, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }
    [StructLayout(LayoutKind.Sequential)] public struct BitmapInfo { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int X, Y; public uint Used, Important; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] public struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public MouseInput Mouse; [FieldOffset(8)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] public struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] public struct KeyboardInput { public ushort Vk, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] public struct KeyHook { public uint Vk, Scan, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] public struct MouseHook { public Point Point; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] public struct Message { public IntPtr Window; public uint Id; public UIntPtr WParam; public IntPtr LParam; public uint Time; public Point Point; public uint Private; }
    public delegate bool EnumWindow(IntPtr hwnd, IntPtr data);
    public delegate bool EnumMonitor(IntPtr monitor, IntPtr hdc, ref NativeRect rect, IntPtr data);
    public delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] public static extern IntPtr GetThreadDesktop(uint id);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder buffer, int length, out int needed);
    [DllImport("kernel32.dll")] public static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool GetTokenInformation(IntPtr token, int kind, out int value, int length, out int needed);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindow callback, IntPtr data);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, EnumMonitor callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr monitor, ref Monitor info);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int width, int height, IntPtr src, int sx, int sy, uint operation);
    [DllImport("gdi32.dll")] public static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint first, uint lines, byte[] pixels, ref BitmapInfo info, uint usage);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SetWindowsHookEx(int kind, HookCallback callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint thread, uint message, UIntPtr wparam, IntPtr lparam);
}

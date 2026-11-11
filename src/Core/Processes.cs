using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
namespace DesktopTestDriver;

public static class Processes
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32FirstW(IntPtr snapshot, ref Entry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32NextW(IntPtr snapshot, ref Entry entry);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Entry { public uint Size, Usage, Pid; public IntPtr Heap; public uint Module, Threads, Parent; public int Priority; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe; }
    // (pid, creation time) -> identity. Start time and path are read once per process.
    private static readonly ConcurrentDictionary<(int, long), DesktopProcess> cache = new();
    private const uint QueryLimited = 0x1000;
    public static DesktopProcess Identify(Process process) => Identify(process.Id);
    public static DesktopProcess Identify(int pid)
    {
        var began = Stopwatch.GetTimestamp();
        do
        {
            var handle = OpenProcess(QueryLimited, false, pid);
            if (handle == IntPtr.Zero) throw new GuiException("ProcessNotFound", "ProcessNotFound: " + pid);
            try
            {
                if (!GetProcessTimes(handle, out var created, out _, out _, out _)) throw new GuiException("ObservationUnavailable");
                if (cache.TryGetValue((pid, created), out var known)) return known;
                var buffer = new StringBuilder(32768); var size = buffer.Capacity;
                if (QueryFullProcessImageName(handle, 0, buffer, ref size))
                {
                    var identity = new DesktopProcess(pid, DateTimeOffset.FromFileTime(created).ToUniversalTime(), Path.GetFullPath(buffer.ToString()));
                    if (cache.Count > 4096) cache.Clear();
                    cache[(pid, created)] = identity; return identity;
                }
            }
            finally { Native.CloseHandle(handle); }
            // A new process can exist before its image name becomes queryable.
            Thread.Sleep(5);
        } while (Stopwatch.GetElapsedTime(began).TotalMilliseconds < 500);
        throw new GuiException("ObservationUnavailable", "ProcessImageUnavailable");
    }
    public static bool Alive(DesktopProcess identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.Pid);
            return !process.HasExited && Identify(identity.Pid) == identity;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException or GuiException) { return false; }
    }
    public static bool KillOwned(DesktopProcess identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.Pid);
            if (process.HasExited) return true;
            if (Identify(identity.Pid) != identity) return false;
            process.Kill(false);
            return process.WaitForExit(1500);
        }
        catch (ArgumentException) { return true; }
        catch { return false; }
    }
    public static Process Start(string executable, params string[] args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new GuiException("ObservationUnavailable");
    }
    /// <summary>pid -> parent pid for every process, from one toolhelp snapshot.</summary>
    public static Dictionary<int, int> Parents()
    {
        var result = new Dictionary<int, int>(); var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) return result;
        try
        {
            var entry = new Entry { Size = (uint)Marshal.SizeOf<Entry>() };
            if (Process32FirstW(snapshot, ref entry)) do { result[(int)entry.Pid] = (int)entry.Parent; } while (Process32NextW(snapshot, ref entry));
        }
        finally { Native.CloseHandle(snapshot); }
        return result;
    }
    public static bool Elevated(int pid)
    {
        var handle = OpenProcess(QueryLimited, false, pid); if (handle == IntPtr.Zero) return true;
        try
        {
            if (!Native.OpenProcessToken(handle, 8, out var token)) return true;
            try { return Native.GetTokenInformation(token, 20, out int elevated, 4, out _) && elevated != 0; } finally { Native.CloseHandle(token); }
        }
        finally { Native.CloseHandle(handle); }
    }
    public static bool CurrentElevated { get; } = Elevated(Environment.ProcessId);
    private static readonly ConcurrentDictionary<DesktopProcess, bool> elevation = new();
    public static bool ElevatedCached(DesktopProcess process)
    {
        if (elevation.Count > 4096) elevation.Clear();
        return elevation.GetOrAdd(process, p => Elevated(p.Pid));
    }
    public static string Name(DesktopProcess process) => Path.GetFileName(process.ImagePath);
}

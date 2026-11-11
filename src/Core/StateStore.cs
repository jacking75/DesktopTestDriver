using System.Diagnostics;
using System.Text;

namespace DesktopTestDriver;

/// <summary>A plain state folder. Files are written atomically (temp + rename).</summary>
public sealed class StateStore
{
    public string Root { get; }
    private readonly object local = new();
    public StateStore(string root) { Root = Path.GetFullPath(root); Directory.CreateDirectory(Root); }
    public string PathFor(string name)
    {
        if (Path.GetFileName(name) != name) throw new GuiException("InvalidStatePath");
        return Path.Combine(Root, name);
    }
    public bool Exists(string name) => File.Exists(PathFor(name));
    public void Delete(string name) { try { File.Delete(PathFor(name)); } catch (IOException) { } }
    public T Read<T>(string name)
    {
        using var guard = Shared(name) ? IoGuard(name) : null;
        using var file = new FileStream(PathFor(name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file, Encoding.UTF8);
        return GuiProtocol.Decode<T>(reader.ReadToEnd());
    }
    /// <summary>Atomic replace. <paramref name="durable"/> flushes to disk before the rename.</summary>
    public long Write<T>(string name, T value, bool durable = true)
    {
        var bytes = Encoding.UTF8.GetBytes(GuiProtocol.Encode(value));
        using var guard = Shared(name) ? IoGuard(name) : null;
        lock (local)
        {
            var destination = PathFor(name); var temp = PathFor(Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, durable ? FileOptions.WriteThrough : FileOptions.None))
                { file.Write(bytes); if (durable) file.Flush(true); }
                var replacing = Stopwatch.GetTimestamp();
                while (true)
                {
                    try { File.Move(temp, destination, true); break; }
                    // Scanners or readers can briefly deny rename access.
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException && (e.HResult & 0xffff) is 5 or 32 or 33 && Stopwatch.GetElapsedTime(replacing).TotalMilliseconds < 250)
                    { Thread.Sleep(5); }
                }
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        return bytes.Length;
    }
    /// <summary>Only files shared with the watchdog process need a cross-process lock.</summary>
    private static bool Shared(string name) => name is "held-input.json" or "applications.json" || name.StartsWith("launch-intent-", StringComparison.Ordinal);
    private IDisposable IoGuard(string name)
    {
        var mutex = new Mutex(false, "DesktopTestDriver-IO-" + GuiProtocol.Hash(Root.ToUpperInvariant() + ":" + name)[..32]);
        try { if (!mutex.WaitOne(1000)) throw new GuiException("StateLockUnavailable"); }
        catch (AbandonedMutexException) { /* Atomic snapshots remain valid after a writer process terminates. */ }
        catch { mutex.Dispose(); throw; }
        return new MutexLease(mutex);
    }
    public IDisposable InputGuard() => IoGuard("held-input.json");
    private sealed class MutexLease(Mutex mutex) : IDisposable { public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); } }
    public IDisposable Lock(string name = "state.lock", int milliseconds = 500)
    {
        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            try { return new FileStream(PathFor(name), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Stopwatch.GetElapsedTime(start).TotalMilliseconds < milliseconds) { Thread.Sleep(2); }
            catch (IOException) { throw new GuiException(milliseconds <= 1 ? "AlreadyRunning" : "StateLockUnavailable"); }
        }
    }
    /// <summary>Name prefix for kernel objects bound to this state folder.</summary>
    public string KernelName(string purpose) => "Local\\DesktopTestDriver-" + GuiProtocol.Hash(Root.ToUpperInvariant())[..20] + "-" + purpose;
}
public sealed record Deadline(string Boot, long Started, int Milliseconds, DateTimeOffset AcceptedAt)
{
    /// <summary>Windows keeps this counter per boot. When the value is missing the boot time (seconds) is used instead.</summary>
    public static string BootId { get; } = ReadBootId();
    private static string ReadBootId()
    {
        try
        {
            var value = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters", "BootId", null)?.ToString();
            if (!string.IsNullOrEmpty(value)) return value;
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        return FallbackBootId();
    }
    public static string FallbackBootId() => "boot-" + (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Environment.TickCount64 / 1000) / 10;
    public static Deadline Start(int ms) => new(BootId, Stopwatch.GetTimestamp(), ms, DateTimeOffset.UtcNow);
    public int Remaining => Boot != BootId ? 0 : Math.Max(0, Milliseconds - (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(Started).TotalMilliseconds));
}

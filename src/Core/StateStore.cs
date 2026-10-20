using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace DesktopTestDriver;

public sealed class StateStore
{
    public string Root { get; }
    public StateStore(string root)
    {
        Root = Path.GetFullPath(root);
        if (!Directory.Exists(Root))
        {
            var security = new DirectorySecurity(); var sid = WindowsIdentity.GetCurrent().User!;
            security.SetOwner(sid); security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            FileSystemAclExtensions.CreateDirectory(security, Root);
        }
        Check();
    }
    public void Check()
    {
        for (var d = new DirectoryInfo(Root); d != null; d = d.Parent)
            if ((d.Attributes & FileAttributes.ReparsePoint) != 0) throw new GuiException("UnsafeStateDirectory");
        var acl = new DirectoryInfo(Root).GetAccessControl(); var sid = WindowsIdentity.GetCurrent().User!;
        if (!sid.Equals(acl.GetOwner(typeof(SecurityIdentifier)))) throw new GuiException("StateOwnerMismatch");
        foreach (FileSystemAccessRule r in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (r.AccessControlType == AccessControlType.Allow && !sid.Equals(r.IdentityReference)) throw new GuiException("StatePermissionsTooBroad");
    }
    public string PathFor(string name)
    {
        if (Path.GetFileName(name) != name) throw new GuiException("InvalidStatePath");
        var path = Path.Combine(Root, name);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new GuiException("UnsafeStateFile");
        return path;
    }
    public bool Exists(string name) => File.Exists(PathFor(name));
    public T Read<T>(string name)
    {
        using var guard = IoGuard(name);
        using var file = new FileStream(PathFor(name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file, Encoding.UTF8);
        return GuiProtocol.Decode<T>(reader.ReadToEnd());
    }
    public void Write<T>(string name, T value)
    {
        using var guard = IoGuard(name);
        var destination = PathFor(name); var temp = PathFor(Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { file.Write(Encoding.UTF8.GetBytes(GuiProtocol.Encode(value))); file.Flush(true); }
            var replacing = Stopwatch.GetTimestamp();
            while (true)
            {
                try { File.Move(temp, destination, true); break; }
                // Windows scanners or readers can briefly deny rename/delete access.
                // Retry only sharing/access errors; keep the durable write ahead of input.
                catch (Exception e) when (e is IOException or UnauthorizedAccessException &&
                    (e.HResult & 0xffff) is 5 or 32 or 33 && Stopwatch.GetElapsedTime(replacing).TotalMilliseconds < 250)
                { Thread.Sleep(5); }
            }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private IDisposable IoGuard(string name)
    {
        var key = GuiProtocol.Hash(Encoding.UTF8.GetBytes(Root.ToUpperInvariant() + ":" + name));
        var mutex = new Mutex(false, "DesktopTestDriver-IO-" + key, new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = true });
        try { if (!mutex.WaitOne(1000)) throw new GuiException("StateLockUnavailable"); }
        catch (AbandonedMutexException) { /* Atomic snapshots remain valid after a writer process terminates. */ }
        catch { mutex.Dispose(); throw; }
        return new MutexLease(mutex);
    }
    // Input ledger operations are synchronous. A queued kernel mutex avoids starvation
    // of maintenance/watchdog readers during a long sequence of short key writes.
    public IDisposable InputGuard() => IoGuard("input.lock");
    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
    public IDisposable Lock(string name = "state.lock", int milliseconds = 500)
    {
        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            try { return new FileStream(PathFor(name), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Stopwatch.GetElapsedTime(start).TotalMilliseconds < milliseconds) { Thread.Sleep(2); }
            catch (IOException) { throw new GuiException(milliseconds == 1 ? "AlreadyRunning" : "StateLockUnavailable"); }
        }
    }
}
public sealed record Deadline(string Boot, long Started, int Milliseconds, DateTimeOffset AcceptedAt)
{
    // Windows persists this counter per boot. Wall-clock changes do not affect it.
    public static string BootId { get; } = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters", "BootId", null)?.ToString()
        ?? throw new GuiException("BootIdentityUnavailable");
    public static Deadline Start(int ms) => new(BootId, Stopwatch.GetTimestamp(), ms, DateTimeOffset.UtcNow);
    public int Remaining => Boot != BootId ? 0 : Math.Max(0, Milliseconds - (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(Started).TotalMilliseconds));
}

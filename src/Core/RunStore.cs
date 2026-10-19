using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace DesktopTestDriver;

public sealed class RunStore
{
    public string Root { get; }
    public RunConfig Config { get; }
    public string ManifestHash { get; }
    public string PipeName => "DesktopTestDriver-" + Config.RunId;
    public RunStore(string root)
    {
        Root = Path.GetFullPath(root);
        CheckPrivate(Root);
        Config = Wire.Decode<RunConfig>(File.ReadAllText(Path.Combine(Root, "run.json")));
        Config.Validate();
        ManifestHash = Wire.FileHash(Path.Combine(Root, "run.json"));
        if (File.Exists(PathFor("controller.json")))
        {
            Authorization.ValidateDevelopment(Config);
            ControllerFiles.Validate(Read<ControllerConnection>("controller.json"), this);
        }
        else Authorization.Verify(Config);
    }
    public static RunStore Create(string root, string configPath)
    {
        root = Path.GetFullPath(root);
        var config = Wire.Decode<RunConfig>(File.ReadAllText(configPath));
        config.Validate();
        var controllerPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, "controller.json");
        if (!File.Exists(controllerPath)) Authorization.Verify(config);
        if (config.ExpiresAt <= DateTimeOffset.UtcNow) throw new DriverException(Error.Expired);
        // The leaf must be new. Do not change ACLs on an existing user's directory.
        if (Directory.Exists(root)) throw new DriverException(Error.ReservationConflict);
        var parent = Path.GetDirectoryName(root)!;
        if (!Directory.Exists(parent)) throw new DriverException(Error.InvalidRequest);
        CreatePrivateDirectory(root);
        // Never traverse a reparse point, including an ancestor.
        CheckPrivate(root);
        File.WriteAllBytes(Path.Combine(root, "run.json"), File.ReadAllBytes(configPath));
        if (File.Exists(controllerPath)) File.Copy(controllerPath, Path.Combine(root, "controller.json"));
        File.WriteAllText(Path.Combine(root, "secret"), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var store = new RunStore(root);
        store.Write("ledger.json", new InputLedger([], []));
        store.Pulse("server-heartbeat");
        EvidenceVault.Audit(store, null, "delegated", Error.None);
        return store;
    }
    public static void CreatePrivateDirectory(string root)
    {
        if (!Path.IsPathFullyQualified(root) || Directory.Exists(root)) throw new DriverException(Error.ReservationConflict);
        var security = new DirectorySecurity();
        var sid = WindowsIdentity.GetCurrent().User!;
        security.SetOwner(sid);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        FileSystemAclExtensions.CreateDirectory(security, root);
        CheckPrivate(root);
    }
    public static void CheckPrivate(string root)
    {
        for (var d = new DirectoryInfo(root); d != null; d = d.Parent)
            if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0) throw new DriverException(Error.DelegationMismatch);
        var info = new DirectoryInfo(root);
        var acl = info.GetAccessControl();
        var sid = WindowsIdentity.GetCurrent().User!;
        if (!sid.Equals(acl.GetOwner(typeof(SecurityIdentifier)))) throw new DriverException(Error.AuthenticationFailed);
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !sid.Equals(rule.IdentityReference))
                throw new DriverException(Error.AuthenticationFailed);
        foreach (var file in info.EnumerateFileSystemInfos())
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0) throw new DriverException(Error.DelegationMismatch);
    }
    public string PathFor(string name)
    {
        if (System.IO.Path.GetFileName(name) != name) throw new DriverException(Error.InvalidRequest);
        var path = Path.Combine(Root, name);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new DriverException(Error.DelegationMismatch);
        return path;
    }
    public void Write<T>(string name, T value)
    {
        var destination = PathFor(name);
        var temp = PathFor(Guid.NewGuid().ToString("N") + ".tmp");
        using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            var bytes = Encoding.UTF8.GetBytes(Wire.Encode(value));
            file.Write(bytes);
            file.Flush(true);
        }
        try
        {
            // Windows can briefly deny a replace while another reader or a filesystem filter
            // examines the destination. Keep the retry bounded below the shortest lease.
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(temp, destination, true); break; }
                catch (Exception error) when (attempt < 4 && (error is IOException or UnauthorizedAccessException))
                {
                    Thread.Sleep(5 * (attempt + 1));
                }
            }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public T Read<T>(string name)
    {
        using var stream = new FileStream(PathFor(name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Wire.Decode<T>(reader.ReadToEnd());
    }
    public void Pulse(string name) => Write(name, Stopwatch.GetTimestamp());
    public bool Stopped => File.Exists(PathFor("stop.json"));
    public void Stop(Error reason)
    {
        var temp = PathFor(Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(temp, Wire.Encode(new { reason, at = DateTimeOffset.UtcNow }));
        try { File.Move(temp, PathFor("stop.json"), false); }
        catch (IOException) when (Stopped) { File.Delete(temp); }
    }
    public IDisposable Lock(int milliseconds = 1000)
    {
        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            try { return new FileStream(PathFor("ledger.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Stopwatch.GetElapsedTime(start).TotalMilliseconds < milliseconds) { Thread.Sleep(5); }
        }
    }
    public bool Authenticate(string secret)
    {
        var expected = Encoding.UTF8.GetBytes(File.ReadAllText(PathFor("secret")));
        var actual = Encoding.UTF8.GetBytes(secret);
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

public static class Processes
{
    public static ProcessIdentity Identify(Process process)
    {
        // A newly spawned process can exist before its main module becomes queryable.
        var began = Stopwatch.GetTimestamp();
        do
        {
            process.Refresh();
            var path = process.MainModule?.FileName;
            if (path != null) return new(process.Id, process.StartTime.ToUniversalTime(), Path.GetFullPath(path), Wire.FileHash(path));
            if (process.HasExited) break;
            Thread.Sleep(5);
        } while (Stopwatch.GetElapsedTime(began).TotalMilliseconds < 500);
        throw new DriverException(Error.ObservationUnavailable);
    }
    public static bool Alive(ProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.Pid);
            return Identify(process) == identity && !process.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { return false; }
    }
    public static bool KillOwned(ProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.Pid);
            if (process.HasExited) return true;
            if (Identify(process) != identity) return false;
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
        return Process.Start(start) ?? throw new DriverException(Error.ObservationUnavailable);
    }
}

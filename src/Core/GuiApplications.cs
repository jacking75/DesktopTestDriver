using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DesktopTestDriver;

public sealed record OwnedApplication(string ProcessId, string Session, DesktopProcess Identity, string Lifetime) { public string? JobName { get; init; } };
public sealed record LaunchIntent(string ProcessId, string Session, string Lifetime, DesktopProcess? Identity = null);
/// <summary>Processes started by sessions. "session" lifetime processes live in a job object and are stopped with the session.</summary>
public sealed class GuiApplications(StateStore store)
{
    private readonly Dictionary<string, ApplicationJob> jobs = new();
    public DesktopProcess[] Children(string session)
    {
        using var guard = store.Lock("applications.lock");
        return Read().Where(a => a.Session == session && jobs.ContainsKey(a.ProcessId)).SelectMany(a => { try { return jobs[a.ProcessId].Members(); } catch (GuiException) { return []; } }).Distinct().ToArray();
    }
    private OwnedApplication[] Read() => store.Exists("applications.json") ? store.Read<OwnedApplication[]>("applications.json") : [];
    public OwnedApplication[] List(string session) { using var guard = store.Lock("applications.lock"); return Read().Where(a => a.Session == session).ToArray(); }
    public string[] RetainedIds(string session)
    {
        using var guard = store.Lock("applications.lock");
        return Read().Where(a => a.Session == session).Select(a => a.ProcessId).Concat(Directory.GetFiles(store.Root, "launch-intent-*.json")
            .Select(f => store.Read<LaunchIntent>(Path.GetFileName(f))).Where(i => i.Session == session).Select(i => i.ProcessId)).Distinct().ToArray();
    }
    public OwnedApplication Launch(string session, string executable, string[] args, string lifetime, string? workingDirectory, string verb, Action dispatching)
    {
        if (!File.Exists(executable)) throw new GuiException("InvalidExecutable", "ExecutableNotFound") { NoEffect = true };
        if (verb == "runas" && !Processes.CurrentElevated) throw new GuiException("ElevationRequired", "Run the agent elevated to start elevated applications without a UAC prompt") { NoEffect = true };
        if (workingDirectory != null && !Directory.Exists(workingDirectory)) throw new GuiException("InvalidWorkingDirectory") { NoEffect = true };
        using var guard = store.Lock("applications.lock");
        var before = Read();
        var processId = "process-" + Guid.NewGuid().ToString("N")[..20];
        var intentName = "launch-intent-" + processId + ".json";
        var intent = new LaunchIntent(processId, session, lifetime); store.Write(intentName, intent);
        SuspendedApplication suspended;
        dispatching();
        try { suspended = new(executable, args, workingDirectory ?? Path.GetDirectoryName(executable)!); }
        catch (Exception e) { store.Delete(intentName); throw new GuiException("LaunchFailed", e.Message) { NoEffect = true }; }
        using var launch = suspended;
        var process = suspended.Process;
        try
        {
            ApplicationJob? job = null;
            if (lifetime == "session") { job = new ApplicationJob(processId, process); jobs[processId] = job; }
            var owned = new OwnedApplication(processId, session, Processes.Identify(process.Id), lifetime) { JobName = job?.Name };
            store.Write(intentName, intent with { Identity = owned.Identity });
            store.Write("applications.json", before.Append(owned).ToArray()); suspended.Resume(); store.Delete(intentName); return owned;
        }
        catch { if (!process.HasExited) { process.Kill(); if (process.WaitForExit(500)) store.Delete(intentName); } else store.Delete(intentName); throw; }
    }
    /// <summary>ShellExecuteEx: URIs (ms-settings:), control panel items, .msc, .cpl and documents. Not owned: the
    /// target may be handed to a shared process such as explorer.exe, which is never terminated.</summary>
    public static DesktopProcess? Open(string target, string[] args, string verb, string? workingDirectory, Action dispatching)
    {
        if (verb == "runas" && !Processes.CurrentElevated) throw new GuiException("ElevationRequired", "Run the agent elevated; a UAC prompt on the secure desktop cannot be operated") { NoEffect = true };
        var info = new Native.ShellExecuteInfo
        {
            Size = Marshal.SizeOf<Native.ShellExecuteInfo>(), Mask = 0x40 | 0x100 | 0x400, Verb = verb, File = target,
            Parameters = args.Length == 0 ? null : string.Join(" ", args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a)), Directory = workingDirectory, Show = 1
        };
        dispatching();
        if (!Native.ShellExecuteEx(ref info)) throw new GuiException("OpenFailed", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
        if (info.Process == IntPtr.Zero) return null;
        try { var pid = Native.GetProcessId(info.Process); return pid == 0 ? null : Processes.Identify(pid); }
        catch (GuiException) { return null; }
        finally { Native.CloseHandle(info.Process); }
    }
    public bool Terminate(string session, string[]? processIds = null)
    {
        using var guard = store.Lock("applications.lock"); var owned = Read();
        if (processIds != null && processIds.Any(id => !owned.Any(a => a.ProcessId == id && a.Session == session))) throw new GuiException("ProcessNotOwned") { NoEffect = true };
        var targets = owned.Where(a => a.Session == session && (processIds == null ? a.Lifetime == "session" : processIds.Contains(a.ProcessId))).ToArray();
        bool Stop(OwnedApplication application)
        {
            // Ask the identified root to close first. A dialog or a hung app is bounded, then the job is stopped.
            if (Processes.Alive(application.Identity))
            {
                using var root = Process.GetProcessById(application.Identity.Pid);
                if (root.CloseMainWindow()) root.WaitForExit(500);
            }
            if (application.JobName != null && !ApplicationJob.Stop(application.JobName)) return false;
            var result = Processes.KillOwned(application.Identity);
            if (jobs.Remove(application.ProcessId, out var job)) job.Dispose();
            return result;
        }
        var ended = targets.Where(Stop).ToArray();
        store.Write("applications.json", owned.Except(ended).ToArray());
        bool intentsResolved = true;
        foreach (var file in Directory.GetFiles(store.Root, "launch-intent-*.json"))
        {
            var intent = store.Read<LaunchIntent>(Path.GetFileName(file)); if (intent.Session != session) continue;
            if (intent.Identity == null) { if (intent.Lifetime == "session") intentsResolved = false; continue; }
            if (intent.Lifetime == "session" && Processes.KillOwned(intent.Identity)) store.Delete(Path.GetFileName(file));
            else if (intent.Lifetime == "keep")
            {
                var current = Read(); if (!current.Any(a => a.ProcessId == intent.ProcessId)) store.Write("applications.json", current.Append(new(intent.ProcessId, session, intent.Identity, "keep")).ToArray());
                store.Delete(Path.GetFileName(file));
            }
            else intentsResolved = false;
        }
        return ended.Length == targets.Length && intentsResolved;
    }
}

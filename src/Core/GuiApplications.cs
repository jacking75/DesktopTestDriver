using System.Diagnostics;

namespace DesktopTestDriver;

public sealed record OwnedApplication(string ProcessId, string Session, DesktopProcess Identity, string Lifetime);
public sealed record LaunchIntent(string ProcessId, string Session, string Lifetime, DesktopProcess? Identity = null);
public sealed class GuiApplications(StateStore store)
{
    private OwnedApplication[] Read() => store.Exists("applications.json") ? store.Read<OwnedApplication[]>("applications.json") : [];
    public OwnedApplication[] List(string session) { using var guard = store.Lock("applications.lock"); return Read().Where(a => a.Session == session).ToArray(); }
    public string[] RetainedIds(string session)
    {
        using var guard = store.Lock("applications.lock");
        return Read().Where(a => a.Session == session).Select(a => a.ProcessId).Concat(Directory.GetFiles(store.Root, "launch-intent-*.json")
            .Select(f => store.Read<LaunchIntent>(Path.GetFileName(f))).Where(i => i.Session == session).Select(i => i.ProcessId)).Distinct().ToArray();
    }
    public OwnedApplication Launch(string session, string executable, string[] args, string lifetime)
    {
        if (!File.Exists(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new GuiException("InvalidExecutable");
        using var guard = store.Lock("applications.lock");
        var before = Read();
        // Reserve durable storage before starting a process; recovery retains all confirmed ownership.
        store.Write("applications.json", before);
        var processId = "process-" + Guid.NewGuid().ToString("N");
        var intentName = "launch-intent-" + processId + ".json";
        var intent = new LaunchIntent(processId, session, lifetime); store.Write(intentName, intent);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        Process process;
        try { process = Process.Start(start) ?? throw new GuiException("LaunchFailed"); }
        catch { File.Delete(store.PathFor(intentName)); throw; }
        using var ownedProcess = process;
        try
        {
            var owned = new OwnedApplication(processId, session, Processes.Identify(process), lifetime);
            store.Write(intentName, intent with { Identity = owned.Identity });
            store.Write("applications.json", before.Append(owned).ToArray()); File.Delete(store.PathFor(intentName)); return owned;
        }
        catch { if (!process.HasExited) { process.Kill(); if (process.WaitForExit(500)) File.Delete(store.PathFor(intentName)); } else File.Delete(store.PathFor(intentName)); throw; }
    }
    public bool Terminate(string session, string[]? processIds = null)
    {
        using var guard = store.Lock("applications.lock"); var owned = Read();
        if (processIds != null && processIds.Any(id => !owned.Any(a => a.ProcessId == id && a.Session == session))) throw new GuiException("ProcessNotOwned");
        var targets = owned.Where(a => a.Session == session && (processIds == null ? a.Lifetime == "session" : processIds.Contains(a.ProcessId))).ToArray();
        var ended = targets.Where(a => Processes.KillOwned(a.Identity)).ToArray();
        store.Write("applications.json", owned.Except(ended).ToArray());
        bool intentsResolved = true;
        foreach (var file in Directory.GetFiles(store.Root, "launch-intent-*.json"))
        {
            var intent = store.Read<LaunchIntent>(Path.GetFileName(file)); if (intent.Session != session) continue;
            if (intent.Identity == null) { if (intent.Lifetime == "session") intentsResolved = false; continue; }
            if (intent.Lifetime == "session" && Processes.KillOwned(intent.Identity)) File.Delete(file);
            else if (intent.Lifetime == "keep")
            {
                var current = Read(); if (!current.Any(a => a.ProcessId == intent.ProcessId)) store.Write("applications.json", current.Append(new(intent.ProcessId, session, intent.Identity, "keep")).ToArray());
                File.Delete(file);
            }
            else intentsResolved = false;
        }
        return ended.Length == targets.Length && intentsResolved;
    }
}

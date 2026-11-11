using System.Diagnostics;

namespace DesktopTestDriver;

/// <summary>Separate process that cleans up when the agent dies or hangs: releases held keys and buttons and stops
/// session applications. Waits on the agent process handle and a liveness event; nothing is polled on disk.</summary>
public static class WatchdogRunner
{
    public static int Run(string root)
    {
        var store = new StateStore(root); var owner = store.Read<DesktopProcess>("agent-process.json");
        using var once = store.Lock("watchdog.lock", 1);
        using var agent = Process.GetProcessById(owner.Pid);
        if (Processes.Identify(owner.Pid) != owner) return 1;
        using var alive = EventWaitHandle.OpenExisting(store.KernelName("alive"));
        using var stop = EventWaitHandle.OpenExisting(store.KernelName("watchdog-stop"));
        using var exited = new ManualResetEvent(false) { SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(agent.Handle, false) };
        var lastAlive = Stopwatch.GetTimestamp();
        while (true)
        {
            var which = WaitHandle.WaitAny([stop, exited, alive], 1000);
            if (which == 0) return 0;
            if (which == 2) { lastAlive = Stopwatch.GetTimestamp(); continue; }
            var hung = which == WaitHandle.WaitTimeout && Stopwatch.GetElapsedTime(lastAlive).TotalMilliseconds > 3000;
            if (which != 1 && !hung) continue;
            if (stop.WaitOne(0)) return 0;
            // Stop the only input producer before releasing its input independently.
            if (!agent.HasExited && !Processes.KillOwned(owner)) { store.Write("watchdog-recovery.json", new { inputReleased = (bool?)null, reason = "ProducerStopUnconfirmed" }); return 1; }
            bool released;
            try { released = GuiInput.ReleaseLedger(store, new WindowsInput()); } catch { store.Write("watchdog-recovery.json", new { inputReleased = (bool?)null, reason = "InputLedgerUnavailable" }); return 1; }
            var apps = new GuiApplications(store); bool stopped = true;
            foreach (var file in Directory.GetFiles(root, "session-*.json"))
            {
                try { var s = store.Read<SessionInfo>(Path.GetFileName(file)); if (s.Mode == "control" && s.State is not ("closed" or "expired")) stopped &= apps.Terminate(s.ControlSessionId); } catch { stopped = false; }
            }
            store.Write("watchdog-recovery.json", new { inputReleased = released, applicationsStopped = stopped, reason = hung ? "AgentHung" : "AgentExited", at = DateTimeOffset.UtcNow });
            return released && stopped ? 0 : 1;
        }
    }
}

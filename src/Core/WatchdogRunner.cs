using System.Diagnostics;

namespace DesktopTestDriver;

public static class WatchdogRunner
{
    public static async Task<int> Run(string root)
    {
        var store = new StateStore(root); var owner = store.Read<DesktopProcess>("agent-process.json");
        using var once = store.Lock("watchdog.lock", 1);
        var input = new GuiInput(store, new WindowsInput());
        while (!store.Exists("watchdog-stop.json"))
        {
            bool alive = Processes.Alive(owner);
            bool stale;
            try
            {
                var heartbeat = store.Read<long>("agent-heartbeat.json");
                stale = Stopwatch.GetElapsedTime(heartbeat).TotalMilliseconds > 3000 || store.Exists("worker-stop-needed.json") ||
                    input.Overdue(store.Read<int>("agent-limits.json")).Length != 0;
            }
            catch { stale = true; }
            if (!alive || stale)
            {
                // Stop the only input producer before independent release. keep apps are unrelated processes.
                if (alive && !Processes.KillOwned(owner)) { store.Write("watchdog-recovery.json", new { inputReleased = (bool?)null, reason = "ProducerStopUnconfirmed" }); return 1; }
                bool released;
                try { released = input.Release(null); } catch { store.Write("watchdog-recovery.json", new { inputReleased = (bool?)null, reason = "InputLockUnavailable" }); return 1; }
                var apps = new GuiApplications(store); bool stopped = true;
                foreach (var file in Directory.GetFiles(root, "session-*.json"))
                {
                    var s = store.Read<SessionInfo>(Path.GetFileName(file)); if (s.Mode == "control") stopped &= apps.Terminate(s.ControlSessionId);
                }
                store.Write("watchdog-recovery.json", new { inputReleased = released, applicationsStopped = stopped, reason = "AgentUnavailable" }); return released && stopped ? 0 : 1;
            }
            await Task.Delay(50);
        }
        return 0;
    }
}

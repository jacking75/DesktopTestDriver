using System.Diagnostics;

namespace DesktopTestDriver;

public static class WatchdogRunner
{
    public static async Task<bool> Run(RunStore store, ProcessIdentity client, IInputBackend backend, Func<bool> desktopValid, CancellationToken cancellation = default)
    {
        var policy = new WatchdogPolicy(store.Config, Stopwatch.GetTimestamp());
        var input = new InputController(store, backend);
        store.Write("watchdog.json", Processes.Identify(Process.GetCurrentProcess()));
        try
        {
            while (!store.Stopped && !cancellation.IsCancellationRequested)
            {
                bool valid;
                try { valid = desktopValid(); } catch { valid = false; }
                var ledger = store.Read<InputLedger>("ledger.json");
                var reason = policy.Evaluate(Stopwatch.GetTimestamp(), store.Read<long>("client-heartbeat"), store.Read<long>("server-heartbeat"), Processes.Alive(client), valid, ledger);
                if (reason != Error.None) { store.Stop(reason); break; }
                await Task.Delay(25, cancellation);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException) { store.Stop(Error.TransportLost); }
        finally
        {
            if (!store.Stopped) store.Stop(Error.Stopped);
        }
        bool clean = input.Cleanup();
        bool desktop;
        try { desktop = desktopValid(); } catch { desktop = false; }
        store.Write("cleanup.json", new { clean, normalDesktop = desktop, at = DateTimeOffset.UtcNow, ledger = store.Read<InputLedger>("ledger.json") });
        return clean && desktop;
    }
}

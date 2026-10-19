using System.Diagnostics;
using System.Globalization;

namespace DesktopTestDriver;

public sealed record FixturePoint(double X, double Y);
public sealed record FixtureEvent(long Tick, string Kind, int Code, string Action);
public sealed record FixtureSample(string RunId, DateTimeOffset Utc, long Tick, long StartTick, ProcessIdentity Identity, int Session,
    long Window, bool Foreground, int Clicks, int Drag, int Wheel, string Text, FixtureEvent[] Events, bool ObservationValid,
    bool ObservationOverflow, int[] HeldVirtualKeys, FixturePoint Button);
public sealed record FixtureObservation(FixtureSample Sample, WindowIdentity Window);

public interface IFixtureLauncher
{
    Process Start(FixtureScope scope, RunStore store, int seconds);
}
public sealed class FixtureLauncher : IFixtureLauncher
{
    public Process Start(FixtureScope scope, RunStore store, int seconds)
    {
        // The fixture is the deliberately visible test target; Broker/watchdog helpers stay hidden.
        var start = new ProcessStartInfo(scope.ImagePath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Normal };
        foreach (var argument in new[] { store.Config.RunId, store.Root, seconds.ToString(CultureInfo.InvariantCulture) }) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new DriverException(Error.ObservationUnavailable);
    }
}
public static class OwnedFixture
{
    public static async Task<FixtureObservation> Start(RunStore store, IDesktop desktop, IFixtureLauncher launcher, Action checkLive)
    {
        var scope = store.Config.Fixture ?? throw new DriverException(Error.DelegationMismatch);
        if (!store.Config.AllowInput || File.Exists(store.PathFor("input-sealed.json"))) throw new DriverException(Error.DelegationMismatch);
        if (File.Exists(store.PathFor("fixture-intent.json"))) throw new DriverException(Error.ReservationConflict);
        // Validate all ancestors too: a signed path must not be redirected through a junction.
        for (FileSystemInfo? item = new FileInfo(scope.ImagePath); item != null; item = item is FileInfo f ? f.Directory : ((DirectoryInfo)item).Parent)
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new DriverException(Error.DelegationMismatch);
        if (Wire.FileHash(scope.ImagePath) != scope.ImageHash) throw new DriverException(Error.DelegationMismatch);
        checkLive();
        var seconds = Math.Min(scope.MaxSeconds, (int)Math.Floor((store.Config.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds));
        if (seconds < 10) throw new DriverException(Error.Expired);
        using (store.Lock())
        {
            if (store.Stopped) throw new DriverException(Error.Stopped);
            // The fixture waits for registration before displaying a window. A crash in this gap cannot orphan it.
            store.Write("fixture-intent.json", new { scope, at = DateTimeOffset.UtcNow });
            using var process = launcher.Start(scope, store, seconds);
            try
            {
                var identity = Processes.Identify(process);
                if (!identity.ImagePath.Equals(scope.ImagePath, StringComparison.OrdinalIgnoreCase) || identity.ImageHash != scope.ImageHash)
                    throw new DriverException(Error.DelegationMismatch);
                var ledger = store.Read<InputLedger>("ledger.json");
                store.Write("ledger.json", ledger with { Processes = [.. ledger.Processes, new(identity)] });
                store.Write("fixture.json", identity);
            }
            catch { if (!process.HasExited) { process.Kill(false); process.WaitForExit(500); } throw; }
        }
        var began = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(began).TotalMilliseconds < 2500)
        {
            checkLive();
            if (File.Exists(store.PathFor("fixture-input.json"))) return Observe(store, desktop.Inspect());
            await Task.Delay(20);
        }
        throw new DriverException(Error.ObservationUnavailable);
    }
    public static FixtureObservation Observe(RunStore store, Observation observation)
    {
        var scope = store.Config.Fixture ?? throw new DriverException(Error.DelegationMismatch);
        var identity = store.Read<ProcessIdentity>("fixture.json");
        if (!identity.ImagePath.Equals(scope.ImagePath, StringComparison.OrdinalIgnoreCase) || identity.ImageHash != scope.ImageHash ||
            !store.Read<InputLedger>("ledger.json").Processes.Any(p => p.Identity == identity) || !Processes.Alive(identity))
            throw new DriverException(Error.WindowChanged);
        var sample = store.Read<FixtureSample>("fixture-input.json");
        var window = observation.Windows.SingleOrDefault(w => w.Hwnd == sample.Window && w.Pid == identity.Pid && w.CreatedAt == identity.CreatedAt &&
            w.ImagePath.Equals(identity.ImagePath, StringComparison.OrdinalIgnoreCase) && w.ImageHash == identity.ImageHash && w.Title == "DesktopTestDriver marker " + store.Config.RunId);
        if (sample.RunId != store.Config.RunId || sample.Identity != identity || sample.Session != store.Config.TargetSession ||
            observation.Session != sample.Session || observation.Desktop != "Default" || observation.Elevated || window == null ||
            sample.Foreground != (observation.Foreground == sample.Window) || DateTimeOffset.UtcNow - sample.Utc > TimeSpan.FromSeconds(2) || sample.Utc > DateTimeOffset.UtcNow.AddSeconds(1) ||
            !sample.ObservationValid || sample.ObservationOverflow || sample.Events.Length > 4096 || sample.Text.Length > 4096 ||
            sample.Tick < sample.StartTick || sample.Clicks < 0 || sample.Drag < 0 || sample.Wheel < 0)
            throw new DriverException(Error.ObservationUnavailable);
        return new(sample, window);
    }
    // Executes in the child before UI creation and throughout its lifetime, independent of Server transport.
    public static bool MayRun(RunStore store, ProcessIdentity self, long started)
    {
        try
        {
            if (store.Stopped || store.Config.Fixture == null || DateTimeOffset.UtcNow >= store.Config.ExpiresAt ||
                !Processes.Alive(store.Read<ProcessIdentity>("client.json")) || !Processes.Alive(store.Read<ProcessIdentity>("watchdog.json"))) return false;
            if (Stopwatch.GetElapsedTime(store.Read<long>("client-heartbeat")).TotalMilliseconds > store.Config.HeartbeatMilliseconds ||
                Stopwatch.GetElapsedTime(store.Read<long>("server-heartbeat")).TotalMilliseconds > store.Config.HeartbeatMilliseconds) return false;
            if (!File.Exists(store.PathFor("fixture.json"))) return Stopwatch.GetElapsedTime(started).TotalSeconds < 2;
            return store.Read<ProcessIdentity>("fixture.json") == self && store.Read<InputLedger>("ledger.json").Processes.Any(p => p.Identity == self);
        }
        catch { return false; }
    }
}

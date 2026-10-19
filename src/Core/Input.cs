using System.Diagnostics;

namespace DesktopTestDriver;

public interface IInputBackend
{
    bool IsDown(string kind, int code);
    void Down(string kind, int code);
    void Up(string kind, int code);
    void Move(int x, int y);
    void Wheel(int delta);
}
public sealed class InputController(RunStore store, IInputBackend backend)
{
    public void Down(string kind, int code)
    {
        using var locked = store.Lock();
        if (store.Stopped) throw new DriverException(Error.Stopped);
        var ledger = store.Read<InputLedger>("ledger.json");
        if (ledger.Held.Any(i => i.Kind == kind && i.Code == code)) throw new DriverException(Error.InvalidRequest);
        if (kind != "unicode" && backend.IsDown(kind, code)) throw new DriverException(Error.UserInputConflict);
        // Intent is durable before injection: partial SendInput must still be recoverable.
        store.Write("ledger.json", ledger with { Held = [.. ledger.Held, new(kind, code, Stopwatch.GetTimestamp())] });
        backend.Down(kind, code);
    }
    public void Up(string kind, int code)
    {
        using var locked = store.Lock();
        var ledger = store.Read<InputLedger>("ledger.json");
        if (!ledger.Held.Any(i => i.Kind == kind && i.Code == code)) return;
        backend.Up(kind, code);
        store.Write("ledger.json", ledger with { Held = ledger.Held.Where(i => i.Kind != kind || i.Code != code).ToArray() });
    }
    public void Move(int x, int y) { using var locked = store.Lock(); if (store.Stopped) throw new DriverException(Error.Stopped); backend.Move(x, y); }
    public void Wheel(int delta) { using var locked = store.Lock(); if (store.Stopped) throw new DriverException(Error.Stopped); backend.Wheel(delta); }
    public bool ReleaseAll()
    {
        using var locked = store.Lock();
        var ledger = store.Read<InputLedger>("ledger.json");
        var remaining = ledger.Held.ToList();
        foreach (var held in ledger.Held.Reverse())
        {
            try { backend.Up(held.Kind, held.Code); remaining.Remove(held); }
            catch { /* Retain failed entries for independent recovery. */ }
        }
        store.Write("ledger.json", ledger with { Held = remaining.ToArray() });
        return remaining.Count == 0;
    }
    public bool Cleanup()
    {
        var released = ReleaseAll();
        var applicationsStopped = OwnedApplications.Terminate(store);
        using var locked = store.Lock();
        var ledger = store.Read<InputLedger>("ledger.json");
        var remaining = ledger.Processes.Reverse().Where(p => !Processes.KillOwned(p.Identity)).Reverse().ToArray();
        store.Write("ledger.json", ledger with { Processes = remaining });
        return released && applicationsStopped && remaining.Length == 0;
    }
}

public sealed class WatchdogPolicy(RunConfig config, long startedTick)
{
    public Error Evaluate(long now, long clientHeartbeat, long serverHeartbeat, bool clientAlive, bool desktopValid, InputLedger ledger)
    {
        double Age(long tick) => TimeSpan.FromSeconds((now - tick) / (double)Stopwatch.Frequency).TotalMilliseconds;
        if (!clientAlive) return Error.TransportLost;
        if (!desktopValid) return Error.SessionChanged;
        if (Age(startedTick) >= config.MaxSeconds * 1000 || DateTimeOffset.UtcNow >= config.ExpiresAt) return Error.Expired;
        if (Age(clientHeartbeat) > config.HeartbeatMilliseconds || Age(serverHeartbeat) > config.HeartbeatMilliseconds) return Error.TransportLost;
        if (ledger.Held.Any(h => Age(h.SinceTick) >= config.MaxHoldMilliseconds)) return Error.Expired;
        return Error.None;
    }
}

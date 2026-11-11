using System.Diagnostics;

namespace DesktopTestDriver;

public readonly record struct InputEvent(string Kind, int Code, bool Up);
public interface IInputBackend
{
    bool IsDown(string kind, int code);
    /// <summary>Sends all events with one SendInput call.</summary>
    void Send(IReadOnlyList<InputEvent> events);
    void Move(int x, int y);
    void Wheel(int delta);
}
public sealed record HeldInput(string Session, string Kind, int Code, long Tick);
/// <summary>Tracks keys and buttons the driver holds down so they are always released.
/// Only state that can stay pressed is written to the ledger file (read by the watchdog).</summary>
public sealed class GuiInput(StateStore store, IInputBackend backend)
{
    public const int Batch = 500;
    private readonly List<HeldInput> held = Load(store);
    private static List<HeldInput> Load(StateStore store) { try { return store.Exists("held-input.json") ? store.Read<HeldInput[]>("held-input.json").ToList() : []; } catch { return []; } }
    private void Persist() => store.Write("held-input.json", held.ToArray());
    public void Down(string session, string kind, int code, Action? dispatching = null)
    {
        lock (held)
        {
            if (held.Any(h => h.Kind == kind && h.Code == code) || backend.IsDown(kind, code)) throw new GuiException("UserInputConflict", "KeyAlreadyDown") { NoEffect = true };
            held.Add(new(session, kind, code, Stopwatch.GetTimestamp())); Persist();
            dispatching?.Invoke(); backend.Send([new(kind, code, false)]);
        }
    }
    public bool Up(string session, string kind, int code)
    {
        lock (held)
        {
            var item = held.FirstOrDefault(h => h.Session == session && h.Kind == kind && h.Code == code);
            if (item == null) return false;
            backend.Send([new(kind, code, true)]); held.Remove(item); Persist(); return true;
        }
    }
    /// <summary>Presses and releases several keys/buttons as one ledger entry: one write before, one after.</summary>
    public void Press(string session, IReadOnlyList<(string Kind, int Code)> keys, Action? dispatching = null, int repeat = 1)
    {
        lock (held)
        {
            foreach (var k in keys) if (held.Any(h => h.Kind == k.Kind && h.Code == k.Code) || backend.IsDown(k.Kind, k.Code)) throw new GuiException("UserInputConflict", "KeyAlreadyDown") { NoEffect = true };
            var tick = Stopwatch.GetTimestamp(); held.AddRange(keys.Select(k => new HeldInput(session, k.Kind, k.Code, tick))); Persist();
            try
            {
                dispatching?.Invoke();
                var events = new List<InputEvent>();
                // Modifiers stay down while the last key repeats (double click with Ctrl and so on).
                foreach (var k in keys.Take(keys.Count - 1)) events.Add(new(k.Kind, k.Code, false));
                var last = keys[^1];
                for (int i = 0; i < repeat; i++) { events.Add(new(last.Kind, last.Code, false)); events.Add(new(last.Kind, last.Code, true)); }
                foreach (var k in keys.Take(keys.Count - 1).Reverse()) events.Add(new(k.Kind, k.Code, true));
                backend.Send(events);
            }
            catch
            {
                foreach (var k in keys.Reverse()) try { backend.Send([new(k.Kind, k.Code, true)]); } catch { }
                throw;
            }
            finally { held.RemoveAll(h => h.Session == session && h.Tick == tick); Persist(); }
        }
    }
    /// <summary>Unicode characters cannot stay pressed, so they are not recorded. Sent in batches of 500 events;
    /// <paramref name="between"/> runs before each batch and can cancel.</summary>
    public int Type(string text, Action between, Action dispatching)
    {
        var events = new List<InputEvent>(text.Length * 2);
        foreach (var ch in text)
        {
            if (ch == '\n') { events.Add(new("key", 13, false)); events.Add(new("key", 13, true)); }
            else if (ch == '\t') { events.Add(new("key", 9, false)); events.Add(new("key", 9, true)); }
            else { events.Add(new("unicode", ch, false)); events.Add(new("unicode", ch, true)); }
        }
        var sent = 0;
        for (int i = 0; i < events.Count; i += Batch)
        {
            between(); if (i == 0) dispatching();
            var slice = events.GetRange(i, Math.Min(Batch, events.Count - i)); backend.Send(slice); sent += slice.Count;
        }
        return sent;
    }
    public bool Release(string? session)
    {
        lock (held)
        {
            foreach (var item in held.Where(h => session == null || h.Session == session).Reverse().ToArray())
            {
                try { backend.Send([new(item.Kind, item.Code, true)]); held.Remove(item); } catch { }
            }
            Persist(); return !held.Any(h => session == null || h.Session == session);
        }
    }
    public bool Empty(string session) { lock (held) return !held.Any(h => h.Session == session); }
    public string[] Overdue(int maximum) { lock (held) return held.Where(h => Stopwatch.GetElapsedTime(h.Tick).TotalMilliseconds >= maximum).Select(h => h.Session).Distinct().ToArray(); }
    public void Move(int x, int y) => backend.Move(x, y);
    public void Wheel(int delta) => backend.Wheel(delta);
    /// <summary>Releases everything recorded in a ledger file. Used by the watchdog after the agent died.</summary>
    public static bool ReleaseLedger(StateStore store, IInputBackend backend)
    {
        if (!store.Exists("held-input.json")) return true;
        var items = store.Read<HeldInput[]>("held-input.json"); var ok = true;
        foreach (var item in items.Reverse()) try { backend.Send([new(item.Kind, item.Code, true)]); } catch { ok = false; }
        store.Write("held-input.json", Array.Empty<HeldInput>()); return ok;
    }
}

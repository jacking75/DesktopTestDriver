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
public sealed record HeldInput(string Session, string Kind, int Code, long Tick);
public sealed class GuiInput(StateStore store, IInputBackend backend)
{
    private HeldInput[] Read() => store.Exists("held-input.json") ? store.Read<HeldInput[]>("held-input.json") : [];
    public void Down(string session, string kind, int code, Action? dispatching = null)
    {
        using var guard = store.InputGuard(); var held = Read();
        if (held.Any(h => h.Kind == kind && h.Code == code) || kind != "unicode" && backend.IsDown(kind, code)) throw new GuiException("UserInputConflict");
        store.Write("held-input.json", held.Append(new(session, kind, code, Stopwatch.GetTimestamp())).ToArray());
        dispatching?.Invoke(); backend.Down(kind, code);
    }
    public bool Up(string session, string kind, int code)
    {
        using var guard = store.InputGuard(); var held = Read();
        if (!held.Any(h => h.Session == session && h.Kind == kind && h.Code == code)) return false;
        backend.Up(kind, code); store.Write("held-input.json", held.Where(h => !(h.Session == session && h.Kind == kind && h.Code == code)).ToArray()); return true;
    }
    public bool Release(string? session)
    {
        using var guard = store.InputGuard(); var held = Read().ToList();
        foreach (var item in held.Where(h => session == null || h.Session == session).Reverse().ToArray())
        {
            try { backend.Up(item.Kind, item.Code); held.Remove(item); } catch { }
        }
        store.Write("held-input.json", held.ToArray()); return !held.Any(h => session == null || h.Session == session);
    }
    public bool Empty(string session) { using var guard = store.InputGuard(); return !Read().Any(h => h.Session == session); }
    public string[] Overdue(int maximum) { using var guard = store.InputGuard(); return Read().Where(h => Stopwatch.GetElapsedTime(h.Tick).TotalMilliseconds >= maximum).Select(h => h.Session).Distinct().ToArray(); }
    public void Move(int x, int y) => backend.Move(x, y);
    public void Wheel(int delta) => backend.Wheel(delta);
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Principal;

namespace DesktopTestDriver;

public sealed record SessionInfo(string ControlSessionId, string MachineId, string User, int InteractiveSessionId, string Desktop,
    string Mode, string State, int IdleSeconds, DateTimeOffset IdleExpiresAt, bool? InputReleased = true, bool? ApplicationsStopped = true, string? StopReason = null)
{
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string[] TerminatedProcessIds { get; init; } = [];
    public string[] RetainedProcessIds { get; init; } = [];
}
public sealed class ControlSession(SessionInfo info)
{
    public SessionInfo Info = info;
    public readonly object Mutation = new();
    public readonly SemaphoreSlim Queue = new(1);
    public CancellationTokenSource Permit = new();
    public bool CloseRequested;
    public string CloseReason = "SessionClosed";
    public long LastActivity = Stopwatch.GetTimestamp();
    public void Touch() { LastActivity = Stopwatch.GetTimestamp(); Info = Info with { IdleExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Info.IdleSeconds) }; }
}
public sealed class ControlSessions(StateStore store, string machine, int interactiveSession)
{
    private readonly ConcurrentDictionary<string, ControlSession> sessions = new();
    private readonly object creation = new();
    public ControlSession Get(string id) => sessions.TryGetValue(id, out var s) ? s : throw new GuiException("SessionNotFound");
    public SessionInfo[] List() => sessions.Values.Select(s => s.Info).ToArray();
    public void Save(ControlSession session) => store.Write("session-" + session.Info.ControlSessionId + ".json", session.Info);
    public SessionInfo Open(string mode, int idle)
    {
        if (mode is not ("control" or "observe") || idle is < 1 or > 86400) throw new GuiException("InvalidSessionOptions");
        lock (creation)
        {
            if (mode == "control" && sessions.Values.Any(s => s.Info.Mode == "control" && s.Info.State is not ("closed" or "expired"))) throw new GuiException("ControlSessionBusy");
            var info = new SessionInfo("control-" + Guid.NewGuid().ToString("N"), machine, WindowsIdentity.GetCurrent().User!.Value, interactiveSession, "Default", mode, "active", idle, DateTimeOffset.UtcNow.AddSeconds(idle));
            var session = new ControlSession(info); Save(session); sessions[info.ControlSessionId] = session; return info;
        }
    }
    public void Recover()
    {
        foreach (var file in Directory.GetFiles(store.Root, "session-*.json"))
        {
            var info = store.Read<SessionInfo>(Path.GetFileName(file));
            if (info.State is not ("closed" or "expired")) info = info with { State = "blocked", InputReleased = null, ApplicationsStopped = null, StopReason = "AgentRestarted" };
            var s = new ControlSession(info) { CloseRequested = true, CloseReason = "AgentRestarted" }; s.Permit.Cancel(); sessions[info.ControlSessionId] = s; Save(s);
        }
    }
    public void Check(ControlSession s, bool change)
    {
        if (s.Info.State == "expired") throw new GuiException("SessionExpired");
        if (change && (s.Info.State != "active" || s.Permit.IsCancellationRequested)) throw new GuiException("SessionBlocked");
        if (!change && s.Info.State is "closed" or "expired") throw new GuiException("SessionClosed");
        if (change && s.Info.Mode == "observe") throw new GuiException("ReadOnlySession");
    }
    public IEnumerable<ControlSession> Active => sessions.Values;
}

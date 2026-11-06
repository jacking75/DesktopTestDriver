using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Principal;

namespace DesktopTestDriver;

public sealed record SessionInfo(string ControlSessionId, string MachineId, string User, int InteractiveSessionId, string Desktop,
    string Mode, string State, int IdleSeconds, DateTimeOffset IdleExpiresAt, bool? InputReleased = true, bool? ApplicationsStopped = true, string? StopReason = null)
{
    public string? ApplicationProfileId { get; init; }
    public DateTimeOffset? LeaseExpiresAt { get; init; }
    public int ApiVersion { get; init; } = 1;
    public string InteractionPolicy { get; init; } = "background-only";
    public DesktopProcess[] TargetScope { get; init; } = [];
    public long Revision { get; init; }
    public string[] PendingUnknownRequestIds { get; init; } = [];
    public string? PauseReason { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string[] TerminatedProcessIds { get; init; } = [];
    public string[] RetainedProcessIds { get; init; } = [];
}
public sealed class ControlSession(SessionInfo info)
{
    public SessionInfo Info = info;
    public readonly object Mutation = new();
    public readonly SemaphoreSlim Queue = new(1);
    public readonly SemaphoreSlim Releasing = new(1);
    public CancellationTokenSource Permit = new();
    public volatile bool CloseRequested;
    public string CloseReason = "SessionClosed";
    public long LastActivity = Stopwatch.GetTimestamp();
    public void Touch() { LastActivity = Stopwatch.GetTimestamp(); Info = Info with { IdleExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Info.IdleSeconds) }; }
}
public sealed class ControlSessions(StateStore store, string machine, int interactiveSession, string machineUsage = "shared")
{
    private readonly ConcurrentDictionary<string, ControlSession> sessions = new();
    private readonly object creation = new();
    public ControlSession Get(string id) => sessions.TryGetValue(id, out var s) ? s : throw new GuiException("SessionNotFound");
    public SessionInfo[] List() => sessions.Values.Select(s => s.Info).ToArray();
    public void Save(ControlSession session) => store.Write("session-" + session.Info.ControlSessionId + ".json", session.Info);
    public SessionInfo Open(string mode, int idle, int apiVersion = 1, string? policy = null, DesktopProcess[]? targets = null, string? profileId = null)
    {
        if (mode is not ("control" or "observe") || idle is < 1 or > 86400) throw new GuiException("InvalidSessionOptions");
        policy ??= machineUsage == "dedicated" ? "isolated-desktop" : "background-only";
        if (apiVersion is not (1 or 2)) throw new GuiException("UnsupportedApiVersion");
        if (policy is not ("background-only" or "isolated-desktop" or "foreground-exclusive")) throw new GuiException("UnsupportedInteractionPolicy");
        if (policy == "isolated-desktop" && machineUsage != "dedicated") throw new GuiException("DedicatedMachineRequired");
        if (targets?.Any(p => !Processes.Alive(p)) == true) throw new GuiException("ProcessIdentityMismatch");
        lock (creation)
        {
            if (mode == "control" && sessions.Values.Any(s => s.Info.Mode == "control" && s.Info.State is not ("closed" or "expired"))) throw new GuiException("ControlSessionBusy");
            var info = new SessionInfo("control-" + Guid.NewGuid().ToString("N"), machine, WindowsIdentity.GetCurrent().User!.Value, interactiveSession, "Default", mode, "active", idle, DateTimeOffset.UtcNow.AddSeconds(idle)) { ApplicationProfileId = profileId, ApiVersion = apiVersion, InteractionPolicy = policy, TargetScope = targets ?? [] };
            var session = new ControlSession(info); Save(session); sessions[info.ControlSessionId] = session; return info;
        }
    }
    public SessionInfo Pause(string id, string reason)
    {
        var s = Get(id); lock (s.Mutation) { Check(s, false); s.Permit.Cancel(); s.Info = s.Info with { State = "blocked", Revision = s.Info.Revision + 1, PauseReason = reason }; Save(s); return s.Info; }
    }
    public SessionInfo Resume(string id, long expectedRevision)
    {
        var s = Get(id); lock (s.Mutation)
        {
            if (s.Info.Revision != expectedRevision) throw new GuiException("RevisionMismatch");
            if (s.CloseRequested || s.Info.State != "blocked" || s.Info.PendingUnknownRequestIds.Length != 0) throw new GuiException("SessionBlocked");
            s.Permit.Dispose(); s.Permit = new(); s.Info = s.Info with { State = "active", Revision = s.Info.Revision + 1, PauseReason = null }; Save(s); return s.Info;
        }
    }
    public SessionInfo Reconcile(string id, string requestId, string resolution, string evidenceRef, long expectedRevision)
    {
        if (!GuiProtocol.Id(requestId) || !GuiProtocol.Id(evidenceRef) || resolution is not ("effect-confirmed" or "no-effect-confirmed" or "abandoned")) throw new GuiException("InvalidReconciliation");
        var s = Get(id); lock (s.Mutation)
        {
            if (s.Info.Revision != expectedRevision) throw new GuiException("RevisionMismatch");
            if (!s.Info.PendingUnknownRequestIds.Contains(requestId)) throw new GuiException("UnknownRequestNotPending");
            store.Write("reconciliation-" + GuiProtocol.Hash(System.Text.Encoding.UTF8.GetBytes(id + ":" + requestId)) + ".json", new { id, requestId, resolution, evidenceRef, at = DateTimeOffset.UtcNow, originalOutcomePreserved = true });
            s.Info = s.Info with { PendingUnknownRequestIds = s.Info.PendingUnknownRequestIds.Where(x => x != requestId).ToArray(), Revision = s.Info.Revision + 1 };
            Save(s); return s.Info;
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
        if (s.Info.State == "closed") throw new GuiException("SessionClosed");
        if (change && (s.CloseRequested || s.Info.State != "active" || s.Permit.IsCancellationRequested)) throw new GuiException("SessionBlocked");
        if (change && s.Info.Mode == "observe") throw new GuiException("ReadOnlySession");
    }
    public IEnumerable<ControlSession> Active => sessions.Values;
}

using System.Collections.Concurrent;
using System.Diagnostics;

namespace DesktopTestDriver;

/// <summary>Which windows a session may touch besides what it launched or opened itself.</summary>
public sealed record TargetSpec
{
    public int[]? Pids { get; init; }
    public DesktopProcess[]? Processes { get; init; }
    public string[]? Executables { get; init; }
    public string? WindowTitle { get; init; }
    public string? WindowTitleContains { get; init; }
    public string? WindowClass { get; init; }
    public bool Any { get; init; }
    public bool Empty => !Any && Pids is not { Length: > 0 } && Processes is not { Length: > 0 } && Executables is not { Length: > 0 } && WindowTitle == null && WindowTitleContains == null && WindowClass == null;
}
public sealed record SessionInfo(string ControlSessionId, string MachineId, string Mode, bool Input, string State, int IdleSeconds, DateTimeOffset IdleExpiresAt)
{
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClosedAt { get; init; }
    public TargetSpec Targets { get; init; } = new();
    public long Revision { get; init; }
    public string[] PendingUnknownRequestIds { get; init; } = [];
    public string? StopReason { get; init; }
    public string? PauseReason { get; init; }
    /// <summary>Cleanup results; null until the session has been released or closed.</summary>
    public bool? InputReleased { get; init; }
    public bool? ApplicationsStopped { get; init; }
    public DateTimeOffset? LeaseExpiresAt { get; init; }
    public DateTimeOffset? LeaseHardLimitAt { get; init; }
    public string[] TerminatedProcessIds { get; init; } = [];
    public string[] RetainedProcessIds { get; init; } = [];
    public string[] ClosedWindowIds { get; init; } = [];
    public string[] RetainedWindowIds { get; init; } = [];
}
/// <summary>Session-scoped short element references (e1, e2, ...). Oldest entries are dropped past the limit.</summary>
public sealed class ElementRefs
{
    public const int Limit = 8192;
    public sealed record Entry(string Ref, string WindowId, string TreeView, string RuntimeId, string Token);
    private readonly Dictionary<string, Entry> byRef = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> byKey = new(StringComparer.Ordinal);
    private readonly Queue<string> order = new();
    private long next;
    public int Count { get { lock (byRef) return byRef.Count; } }
    public string Export(string windowId, string treeView, string runtimeId, string token)
    {
        var key = windowId + "|" + treeView + "|" + runtimeId + "|" + token;
        lock (byRef)
        {
            if (byKey.TryGetValue(key, out var existing) && byRef.ContainsKey(existing)) return existing;
            var id = "e" + (++next).ToString(System.Globalization.CultureInfo.InvariantCulture);
            byRef[id] = new(id, windowId, treeView, runtimeId, token); byKey[key] = id; order.Enqueue(id);
            while (byRef.Count > Limit && order.TryDequeue(out var old))
                if (byRef.Remove(old, out var dropped)) byKey.Remove(dropped.WindowId + "|" + dropped.TreeView + "|" + dropped.RuntimeId + "|" + dropped.Token);
            return id;
        }
    }
    /// <summary>The caller's token must be a prefix (at least 8 characters) of the full token.</summary>
    public Entry Resolve(string reference, string token, string windowId)
    {
        lock (byRef)
        {
            if (!byRef.TryGetValue(reference, out var entry)) throw new GuiException("StaleElement", "UnknownOrExpiredReference") { NoEffect = true };
            if (entry.WindowId != windowId) throw new GuiException("TargetScopeMismatch", "ReferenceBelongsToAnotherWindow") { NoEffect = true };
            if (token.Length < 8 || !entry.Token.StartsWith(token, StringComparison.Ordinal)) throw new GuiException("StaleElement", "SnapshotTokenMismatch") { NoEffect = true };
            return entry;
        }
    }
    public static string Short(string token) => token.Length > 12 ? token[..12] : token;
}
/// <summary>Session-scoped window IDs (w1, w2, ...), stable for the same handle and generation.</summary>
public sealed class WindowRefs
{
    private readonly Dictionary<string, DesktopWindow> byId = new(StringComparer.Ordinal);
    private readonly Dictionary<(long, long), string> byHandle = new();
    private long next;
    public string Register(DesktopWindow window)
    {
        lock (byId)
        {
            if (byHandle.TryGetValue((window.Handle, window.Generation), out var id) && byId.TryGetValue(id, out var known) && known.Process == window.Process) { byId[id] = window; return id; }
            id = "w" + (++next).ToString(System.Globalization.CultureInfo.InvariantCulture);
            byId[id] = window; byHandle[(window.Handle, window.Generation)] = id; return id;
        }
    }
    public DesktopWindow Get(string id) { lock (byId) return byId.TryGetValue(id, out var w) ? w : throw new GuiException("WindowNotFound", "UnknownWindowId") { NoEffect = true }; }
    public string? IdOf(long handle) { lock (byId) return byHandle.Where(p => p.Key.Item1 == handle).Select(p => p.Value).LastOrDefault(); }
    public KeyValuePair<string, DesktopWindow>[] All { get { lock (byId) return byId.ToArray(); } }
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
    public readonly ElementRefs Elements = new();
    public readonly WindowRefs Windows = new();
    /// <summary>Top-level windows created by an open operation of this session.</summary>
    public readonly ConcurrentDictionary<long, string> OpenedWindows = new();
    /// <summary>Processes started by open that are not owned (not stopped on close) but are in scope.</summary>
    public readonly ConcurrentDictionary<int, DesktopProcess> OpenedProcesses = new();
    /// <summary>Processes with an in-scope window; their untitled popups (menus, drop-downs) are in scope too.</summary>
    public readonly ConcurrentDictionary<int, DesktopProcess> PopupProcesses = new();
    public void Touch() { LastActivity = Stopwatch.GetTimestamp(); Info = Info with { IdleExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Info.IdleSeconds) }; }
}
public sealed class ControlSessions(StateStore store, string machine)
{
    public static readonly TimeSpan ClosedRetention = TimeSpan.FromHours(24);
    private readonly ConcurrentDictionary<string, ControlSession> sessions = new();
    private readonly object creation = new();
    public ControlSession Get(string id) => sessions.TryGetValue(id, out var s) ? s : throw new GuiException("SessionNotFound") { NoEffect = true };
    public SessionInfo[] List(bool all = false) => sessions.Values.Select(s => s.Info).Where(i => all || i.State is not ("closed" or "expired")).OrderBy(i => i.CreatedAt).ToArray();
    public void Save(ControlSession session) => store.Write("session-" + session.Info.ControlSessionId + ".json", session.Info, durable: false);
    public SessionInfo Open(string mode, bool input, int idle, TargetSpec? targets = null)
    {
        if (mode is not ("control" or "observe") || idle is < 1 or > 86400 || input && mode == "observe") throw new GuiException("InvalidSessionOptions");
        targets ??= new();
        if (targets.Pids is { Length: > 0 })
        {
            var identities = new List<DesktopProcess>();
            foreach (var pid in targets.Pids)
            {
                try { using var p = Process.GetProcessById(pid); identities.Add(Processes.Identify(p)); }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or GuiException) { throw new GuiException("ProcessNotFound", "ProcessNotFound: " + pid); }
            }
            targets = targets with { Processes = (targets.Processes ?? []).Concat(identities).Distinct().ToArray() };
        }
        if (targets.Executables != null) targets = targets with { Executables = targets.Executables.Select(Path.GetFullPath).ToArray() };
        lock (creation)
        {
            // Only one session may drive the real keyboard and mouse at a time.
            if (input && sessions.Values.Any(s => s.Info.Input && s.Info.State is not ("closed" or "expired"))) throw new GuiException("ControlSessionBusy", "AnotherInputSessionIsOpen");
            var info = new SessionInfo("control-" + Guid.NewGuid().ToString("N")[..20], machine, mode, input, "active", idle, DateTimeOffset.UtcNow.AddSeconds(idle)) { Targets = targets };
            var session = new ControlSession(info); Save(session); sessions[info.ControlSessionId] = session; return info;
        }
    }
    public SessionInfo Pause(string id, string reason)
    {
        var s = Get(id); lock (s.Mutation) { Check(s, false); s.Permit.Cancel(); s.Info = s.Info with { State = "blocked", Revision = s.Info.Revision + 1, PauseReason = reason }; Save(s); return s.Info; }
    }
    public SessionInfo Resume(string id, long? expectedRevision)
    {
        var s = Get(id); lock (s.Mutation)
        {
            if (expectedRevision != null && s.Info.Revision != expectedRevision) throw new GuiException("RevisionMismatch");
            if (s.CloseRequested || s.Info.State is "closed" or "expired") throw new GuiException("SessionClosed");
            if (s.Info.PendingUnknownRequestIds.Length != 0) throw new GuiException("SessionBlocked", "UnconfirmedRequestsPending", new { pendingUnknownRequestIds = s.Info.PendingUnknownRequestIds });
            if (s.Info.State == "active") return s.Info;
            s.Permit.Dispose(); s.Permit = new(); s.Info = s.Info with { State = "active", Revision = s.Info.Revision + 1, PauseReason = null, StopReason = null }; Save(s); return s.Info;
        }
    }
    public SessionInfo Reconcile(string id, string requestId, string resolution, string? evidenceRef, long? expectedRevision)
    {
        if (!GuiProtocol.Id(requestId) || evidenceRef != null && !GuiProtocol.Id(evidenceRef) || resolution is not ("effect-confirmed" or "no-effect-confirmed" or "abandoned")) throw new GuiException("InvalidReconciliation");
        var s = Get(id); lock (s.Mutation)
        {
            if (expectedRevision != null && s.Info.Revision != expectedRevision) throw new GuiException("RevisionMismatch");
            if (!s.Info.PendingUnknownRequestIds.Contains(requestId)) throw new GuiException("UnknownRequestNotPending");
            store.Write("reconciliation-" + GuiProtocol.Hash(id + ":" + requestId)[..32] + ".json", new { id, requestId, resolution, evidenceRef, at = DateTimeOffset.UtcNow });
            s.Info = s.Info with { PendingUnknownRequestIds = s.Info.PendingUnknownRequestIds.Where(x => x != requestId).ToArray(), Revision = s.Info.Revision + 1 };
            Save(s); return s.Info;
        }
    }
    public void Recover()
    {
        foreach (var file in Directory.GetFiles(store.Root, "session-*.json"))
        {
            SessionInfo info;
            try { info = store.Read<SessionInfo>(Path.GetFileName(file)); } catch { continue; }
            if (info.State is not ("closed" or "expired")) info = info with { State = "blocked", InputReleased = null, ApplicationsStopped = null, StopReason = "AgentRestarted" };
            var s = new ControlSession(info) { CloseRequested = info.State is not ("closed" or "expired"), CloseReason = "AgentRestarted" }; s.Permit.Cancel(); sessions[info.ControlSessionId] = s; Save(s);
        }
    }
    /// <summary>Removes closed or expired sessions after the retention period.</summary>
    public int Cleanup()
    {
        var removed = 0;
        foreach (var s in sessions.Values.ToArray())
        {
            if (s.Info.State is not ("closed" or "expired") || s.Info.ClosedAt is not { } closed || DateTimeOffset.UtcNow - closed < ClosedRetention) continue;
            if (sessions.TryRemove(s.Info.ControlSessionId, out _)) { store.Delete("session-" + s.Info.ControlSessionId + ".json"); removed++; }
        }
        return removed;
    }
    public void Check(ControlSession s, bool change)
    {
        if (s.Info.State == "expired") throw new GuiException("SessionExpired") { NoEffect = true };
        if (s.Info.State == "closed") throw new GuiException("SessionClosed") { NoEffect = true };
        if (change && s.Info.Mode == "observe") throw new GuiException("ReadOnlySession") { NoEffect = true };
        if (change && (s.CloseRequested || s.Info.State != "active" || s.Permit.IsCancellationRequested)) throw new GuiException("SessionBlocked", s.Info.PauseReason ?? s.Info.StopReason ?? "SessionBlocked") { NoEffect = true };
    }
    public IEnumerable<ControlSession> Active => sessions.Values;
}

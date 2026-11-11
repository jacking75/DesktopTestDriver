using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DesktopTestDriver;

public sealed record AgentCommand(GuiRequest Request, int RemainingMilliseconds);
/// <summary>What happened to the application during one request. Decides rejected / failed / completionUnknown.</summary>
public sealed class Effect
{
    /// <summary>Something that can change the application was started.</summary>
    public bool Started;
    /// <summary>Exactly what was sent is known (real input), so a failure is "failed" rather than "unknown".</summary>
    public bool Known;
    public int InputEvents;
}
public sealed class GuiRequestHandler
{
    private readonly IGuiDesktop desktop;
    private readonly ControlSessions sessions;
    private readonly GuiInput input;
    private readonly int maxStop;
    public RequestJournal Journal { get; }
    public CaptureStore Captures { get; }
    public GuiApplications Applications { get; }
    public InteractionGuard? Interaction { get; init; }
    public string MachineId { get; init; } = "local";
    public GuiRequestHandler(StateStore store, IGuiDesktop desktop, ControlSessions sessions, GuiInput input, int maxStop = 3000)
    {
        this.desktop = desktop; this.sessions = sessions; this.input = input; this.maxStop = maxStop;
        Journal = new(store); Journal.Recover(); Captures = new(store, desktop); Applications = new(store);
    }
    public GuiResponse Result(string session, string request) => Journal.Result(session, request);
    private sealed record Ctx(GuiRequest Request, OperationDescriptor Descriptor, ControlSession Session, CancellationToken Cancellation, Effect Effect, long Deadline)
    {
        public JsonElement A => Request.Arguments;
        public string T(string name) => GuiProtocol.Text(Request.Arguments, name);
        public int RemainingMilliseconds => (int)Math.Max(0, (Deadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency);
    }
    public async Task<GuiResponse> Execute(AgentCommand command)
    {
        var started = Stopwatch.GetTimestamp(); var r = command.Request;
        GuiResponse Done(GuiResponse response) => response with { RequestId = r.RequestId, ControlSessionId = r.ControlSessionId, Operation = r.Operation, MachineId = MachineId, ElapsedMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds };
        GuiResponse Reject(GuiException e) => Done(GuiResponse.Reject(e.Code, r, e.Details, e.Message));
        OperationDescriptor d; ControlSession s;
        try { GuiProtocol.Validate(r); d = Operations.Get(r.Operation); s = sessions.Get(r.ControlSessionId); }
        catch (GuiException e) { return Reject(e); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { return Done(GuiResponse.Reject("InvalidRequest", r, message: e.Message)); }
        if (!d.ReadOnly && s.Info.Mode == "observe") return Done(GuiResponse.Reject("ReadOnlySession", r));
        if (d.Input && !s.Info.Input) return Done(GuiResponse.Reject("InputSessionRequired", r, message: "Open the session with --input to send real keyboard and mouse input"));
        var changing = !d.ReadOnly; var remaining = Math.Clamp(command.RemainingMilliseconds, 1, r.TimeoutMilliseconds);
        var permit = CancellationToken.None;
        if (d.Journaled)
        {
            lock (Journal.Gate(r.ControlSessionId, r.RequestId))
            {
                var existing = Journal.Find(r.ControlSessionId, r.RequestId);
                if (existing != null) return existing.Fingerprint != GuiProtocol.Fingerprint(r) ? Done(GuiResponse.Reject("DuplicateRequestMismatch", r)) : existing.Response;
                lock (s.Mutation)
                {
                    try
                    {
                        if (r.Operation != "release-input") sessions.Check(s, changing);
                        Journal.Accept(r, Deadline.Start(remaining), MachineId);
                    }
                    catch (GuiException e) { return Reject(e); }
                    if (changing && r.Operation != "release-input") permit = s.Permit.Token;
                }
            }
        }
        else
        {
            try { sessions.Check(s, false); } catch (GuiException e) { return Reject(e); }
        }
        if (r.Operation != "release-input") s.Touch();
        var effect = new Effect(); var acquired = false; GuiResponse response;
        try
        {
            if (r.Operation == "release-input")
            {
                var released = await Release(s, false, "ExplicitRelease");
                response = new(released ? "completed" : "failed", Data: new { inputReleased = released, state = s.Info.State }, Error: released ? null : new("InputReleaseFailed", "InputReleaseFailed"));
            }
            else
            {
                using var timeout = new CancellationTokenSource(remaining);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, permit);
                try { await s.Queue.WaitAsync(linked.Token); acquired = true; }
                catch (OperationCanceledException) { throw new GuiException(permit.IsCancellationRequested ? "SessionBlocked" : "RequestTimeout") { NoEffect = true }; }
                if (changing) lock (s.Mutation) sessions.Check(s, true);
                if (!desktop.Valid) throw new GuiException("UnsupportedDesktop") { NoEffect = true };
                var ctx = new Ctx(r, d, s, linked.Token, effect, started + (long)(remaining * (double)Stopwatch.Frequency / 1000));
                var data = await Task.Run(() => Perform(ctx), CancellationToken.None);
                response = new("completed", Data: data);
                if (changing) Interaction?.Extend(s);
            }
        }
        catch (Exception e)
        {
            var g = e as GuiException;
            var code = g?.Code ?? (e is OperationCanceledException ? "RequestTimeout" : "OperationFailed");
            // rejected: nothing was dispatched. failed: real input was partly sent and exactly what was sent is known.
            // completionUnknown: a dispatch began and its end could not be confirmed.
            var status = d.ReadOnly ? "rejected" : effect.Known && effect.InputEvents > 0 ? "failed" : !effect.Started || g?.NoEffect == true ? "rejected" : effect.Known ? "failed" : "completionUnknown";
            JsonNode? details = g?.Details == null ? null : JsonSerializer.SerializeToNode(g.Details, GuiProtocol.Json);
            if (d.Input && changing)
            {
                var released = false;
                try { lock (s.Mutation) { released = input.Release(s.Info.ControlSessionId); if (!released) { s.Info = s.Info with { State = "blocked", InputReleased = false, StopReason = "InputReleaseFailed" }; sessions.Save(s); } } } catch { }
                details ??= new JsonObject();
                if (details is JsonObject o) { o["inputEventsSent"] = effect.InputEvents; o["inputReleased"] = released; }
            }
            if (code is "ElementNotFound" or "StaleElement" or "ElementDisabled" or "TargetObscured" && d.UsesWindow && GuiProtocol.Flag(r.Arguments, "errorContext", true))
            {
                var context = ErrorContext(s, r);
                if (context != null) { details ??= new JsonObject(); if (details is JsonObject o) o["context"] = context; }
            }
            response = new(status, Error: new(code, g?.Message ?? (e is OperationCanceledException ? code : e.GetType().Name + ": " + e.Message), details));
        }
        finally { if (acquired) s.Queue.Release(); }
        if (response.Status == "completionUnknown" && changing)
        {
            lock (s.Mutation)
            {
                s.Permit.Cancel();
                s.Info = s.Info with { State = "blocked", PauseReason = "UnconfirmedEffect", Revision = s.Info.Revision + 1, PendingUnknownRequestIds = s.Info.PendingUnknownRequestIds.Append(r.RequestId).Distinct().ToArray() };
                sessions.Save(s);
            }
        }
        response = Done(response);
        if (d.Journaled) Journal.Finish(r, response);
        return response;
    }
    // ------------------------------------------------------------------ release / close
    public async Task<bool> Release(ControlSession s, bool close, string reason)
    {
        var started = Stopwatch.GetTimestamp();
        int Left() => Math.Max(0, maxStop - (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        if (close) { lock (s) { s.CloseReason = reason; s.CloseRequested = true; } }
        s.Permit.Cancel();
        if (!await s.Releasing.WaitAsync(maxStop)) return false;
        try
        {
            lock (s.Mutation) { if (s.Info.State is "closed" or "expired") return s.Info.InputReleased == true && s.Info.ApplicationsStopped == true; }
            // A request stuck in a provider call holds the queue: stopping its worker unblocks it.
            if (!await s.Queue.WaitAsync(Left() / 2))
            {
                desktop.ReleaseAutomation(s.Info.ControlSessionId);
                if (!await s.Queue.WaitAsync(Left()))
                {
                    lock (s.Mutation) { s.Info = s.Info with { State = "blocked", InputReleased = null, StopReason = "WorkerStopUnconfirmed" }; sessions.Save(s); }
                    return false;
                }
            }
            try
            {
                var closing = s.CloseRequested; var closeReason = s.CloseReason;
                string[] closedWindows = [], retainedWindows = [];
                if (closing && s.Info.Mode == "control") (closedWindows, retainedWindows) = CloseOpenedWindows(s);
                lock (s.Mutation)
                {
                    Interaction?.Release(s.Info.ControlSessionId);
                    var released = s.Info.Input ? input.Release(s.Info.ControlSessionId) : input.Empty(s.Info.ControlSessionId);
                    var before = Applications.List(s.Info.ControlSessionId);
                    var stopped = !closing || s.Info.Mode == "observe" || Applications.Terminate(s.Info.ControlSessionId);
                    var after = Applications.List(s.Info.ControlSessionId);
                    if (closing) desktop.ReleaseAutomation(s.Info.ControlSessionId);
                    var state = closing && released && stopped ? closeReason == "IdleExpired" ? "expired" : "closed"
                        : !closing && released && s.Info.PendingUnknownRequestIds.Length == 0 && s.Info.PauseReason == null && reason is "ExplicitRelease" or "RequestTimeout" ? "active" : "blocked";
                    s.Info = s.Info with
                    {
                        State = state, InputReleased = released, ApplicationsStopped = stopped, StopReason = state == "active" ? null : reason,
                        ClosedAt = state is "closed" or "expired" ? DateTimeOffset.UtcNow : null,
                        TerminatedProcessIds = s.Info.TerminatedProcessIds.Concat(before.Except(after).Select(a => a.ProcessId)).Distinct().ToArray(),
                        RetainedProcessIds = Applications.RetainedIds(s.Info.ControlSessionId),
                        ClosedWindowIds = s.Info.ClosedWindowIds.Concat(closedWindows).Distinct().ToArray(), RetainedWindowIds = retainedWindows,
                        LeaseExpiresAt = null, LeaseHardLimitAt = null
                    };
                    // A fresh permit lets blocked sessions keep observing; an active session can mutate again.
                    s.Permit = new(); sessions.Save(s);
                    if (state == "active" && s.Info.Input) { try { Interaction?.Arm(s); } catch (GuiException) { s.Info = s.Info with { State = "blocked", StopReason = "ActivityMonitorUnavailable" }; sessions.Save(s); } }
                    return released && stopped;
                }
            }
            finally { s.Queue.Release(); }
        }
        finally { s.Releasing.Release(); }
    }
    /// <summary>Windows created by open cannot be put in a job. They are asked to close (WindowPattern, then WM_CLOSE).</summary>
    private (string[] Closed, string[] Retained) CloseOpenedWindows(ControlSession s)
    {
        var closed = new List<string>(); var retained = new List<string>();
        foreach (var (handle, origin) in s.OpenedWindows.ToArray())
        {
            if (origin != "opened") continue;
            var id = s.Windows.IdOf(handle) ?? "hwnd-" + handle;
            DesktopWindow w;
            try { w = desktop.Identify(handle); } catch (GuiException) { closed.Add(id); continue; }
            try { desktop.Automation(s.Info.ControlSessionId, new UiaCommand(w, "window-state", JsonSerializer.SerializeToElement(new { state = "close" })), CancellationToken.None); }
            catch (GuiException) { try { desktop.Close(w); } catch (GuiException) { } }
            var gone = false;
            for (int i = 0; i < 20 && !gone; i++) { try { desktop.Identify(handle); Thread.Sleep(50); } catch (GuiException) { gone = true; } }
            (gone ? closed : retained).Add(id);
        }
        s.OpenedWindows.Clear();
        return (closed.ToArray(), retained.ToArray());
    }
    // ------------------------------------------------------------------ scope and windows
    private sealed class Scope(GuiRequestHandler handler, ControlSession s)
    {
        private readonly HashSet<DesktopProcess> processes = Processes(handler, s);
        private Dictionary<int, int>? parents;
        private static HashSet<DesktopProcess> Processes(GuiRequestHandler handler, ControlSession s)
        {
            var set = new HashSet<DesktopProcess>(s.Info.Targets.Processes ?? []);
            foreach (var app in handler.Applications.List(s.Info.ControlSessionId)) set.Add(app.Identity);
            foreach (var child in handler.Applications.Children(s.Info.ControlSessionId)) set.Add(child);
            foreach (var opened in s.OpenedProcesses.Values) set.Add(opened);
            return set;
        }
        /// <summary>In scope through targets, owned or opened processes, child processes, window conditions or opened windows.</summary>
        public bool Direct(DesktopWindow w)
        {
            var t = s.Info.Targets;
            if (t.Any || processes.Contains(w.Process) || s.OpenedWindows.ContainsKey(w.Handle)) return true;
            if (t.Executables?.Any(e => string.Equals(e, w.Process.ImagePath, StringComparison.OrdinalIgnoreCase)) == true) return true;
            if (t.WindowTitle != null && w.Title == t.WindowTitle || t.WindowTitleContains != null && w.Title.Contains(t.WindowTitleContains, StringComparison.Ordinal) || t.WindowClass != null && w.ClassName == t.WindowClass) return true;
            if (processes.Count == 0) return false;
            parents ??= DesktopTestDriver.Processes.Parents();
            var pid = w.Process.Pid;
            for (int i = 0; i < 8 && parents.TryGetValue(pid, out var parent) && parent != 0 && parent != pid; i++)
            {
                if (processes.Any(p => p.Pid == parent)) return true;
                pid = parent;
            }
            return false;
        }
        public DesktopWindow[] Filter(DesktopWindow[] all)
        {
            var byHandle = all.ToDictionary(w => w.Handle);
            var inScope = new HashSet<long>(all.Where(Direct).Select(w => w.Handle));
            // Owned windows follow their owner; popups (menus, drop-downs) follow any in-scope window of their process.
            for (int pass = 0; pass < 3; pass++)
                foreach (var w in all) if (!inScope.Contains(w.Handle) && w.OwnerHandle != 0 && inScope.Contains(w.OwnerHandle)) inScope.Add(w.Handle);
            var pids = all.Where(w => inScope.Contains(w.Handle)).Select(w => w.Process).ToHashSet();
            foreach (var w in all) if (w.Kind == "popup" && pids.Contains(w.Process)) inScope.Add(w.Handle);
            foreach (var p in pids) s.PopupProcesses[p.Pid] = p;
            return all.Where(w => inScope.Contains(w.Handle)).ToArray();
        }
        public bool Contains(DesktopWindow w)
        {
            if (Direct(w)) return true;
            if (w.Kind == "popup" && s.PopupProcesses.TryGetValue(w.Process.Pid, out var p) && p == w.Process) return true;
            var owner = w.OwnerHandle;
            for (int i = 0; i < 8 && owner != 0; i++)
            {
                DesktopWindow ow; try { ow = handler.desktop.Identify(owner); } catch (GuiException) { return false; }
                if (Direct(ow)) return true; owner = ow.OwnerHandle;
            }
            return false;
        }
    }
    private PublicWindow Publish(ControlSession s, DesktopWindow w, Dictionary<long, DesktopWindow>? peers = null)
    {
        var id = s.Windows.Register(w);
        string? owner = null;
        if (w.OwnerHandle != 0 && (peers == null || peers.ContainsKey(w.OwnerHandle))) owner = s.Windows.IdOf(w.OwnerHandle) ?? (peers != null ? s.Windows.Register(peers[w.OwnerHandle]) : null);
        return new PublicWindow(id, w.Title, w.Process.Pid, Processes.Name(w.Process), w.Bounds, w.Foreground) { ClassName = w.ClassName, Kind = w.Kind, OwnerWindowId = owner, Minimized = w.Minimized ? true : null };
    }
    private PublicWindow[] ListWindows(ControlSession s)
    {
        var scoped = new Scope(this, s).Filter(desktop.Windows());
        var peers = scoped.ToDictionary(w => w.Handle);
        foreach (var w in scoped) s.Windows.Register(w);
        return scoped.Select(w => Publish(s, w, peers)).ToArray();
    }
    private DesktopWindow Window(Ctx c, string? id = null)
    {
        id ??= c.T("windowId");
        var known = c.Session.Windows.Get(id);
        var current = desktop.Identify(known.Handle);
        if (!WindowQuery.Same(known, current)) throw new GuiException("WindowChanged", "WindowReplacedOrHandleReused") { NoEffect = true };
        if (!new Scope(this, c.Session).Contains(current)) throw new GuiException("TargetScopeMismatch") { NoEffect = true };
        // Windows blocks a normal-privilege process from driving an elevated window (UIPI); say so instead of failing obscurely.
        if (!Processes.CurrentElevated && Processes.ElevatedCached(current.Process)) throw new GuiException("TargetElevated", "Run the agent elevated to drive this window") { NoEffect = true };
        return current;
    }
    // ------------------------------------------------------------------ element references
    private ObservationNode Export(ControlSession s, string windowId, string view, ObservationNode n, IReadOnlyDictionary<string, string>? parents = null)
    {
        var reference = s.Elements.Export(windowId, view, n.ElementId, n.SnapshotToken);
        var parent = n.ParentId != null && parents != null && parents.TryGetValue(n.ParentId, out var p) ? p : null;
        return n with { ElementId = reference, ParentId = parent, SnapshotToken = ElementRefs.Short(n.SnapshotToken) };
    }
    private ObservationNode[] ExportAll(ControlSession s, string windowId, string view, IEnumerable<ObservationNode> nodes)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal); var result = new List<ObservationNode>();
        foreach (var n in nodes) { var e = Export(s, windowId, view, n, map); map[n.ElementId] = e.ElementId; result.Add(e); }
        return result.ToArray();
    }
    private static ElementLocator Locate(Ctx c, string idField = "elementId", string tokenField = "snapshotToken", JsonElement? source = null)
    {
        var a = source ?? c.A;
        var entry = c.Session.Elements.Resolve(GuiProtocol.Text(a, idField), GuiProtocol.Text(a, tokenField), c.T("windowId"));
        return new(entry.RuntimeId, entry.Token, entry.TreeView);
    }
    private static string View(Ctx c) => GuiProtocol.OptionalText(c.A, "treeView") ?? "control";
    private UiaCommand Command(Ctx c, DesktopWindow w, string? operation = null) => new(w, operation ?? c.Request.Operation, c.A)
    {
        TreeView = View(c), Completeness = GuiProtocol.OptionalText(c.A, "completeness") ?? "default",
        SearchRoot = GuiProtocol.Has(c.A, "searchRootElementId") ? Locate(c, "searchRootElementId", "searchRootSnapshotToken") : GuiProtocol.Has(c.A, "rootElementId") ? Locate(c, "rootElementId", "rootSnapshotToken") : null
    };
    private UiaResult Uia(Ctx c, UiaCommand command) => desktop.Automation(c.Session.Info.ControlSessionId, command, c.Cancellation);
    private object? ExportResult(Ctx c, SelectorResult r, string windowId, string view, bool includePath)
    {
        if (r.Error != null) return new { error = ExportError(c.Session, windowId, view, r.Error) };
        var target = Export(c.Session, windowId, view, r.Target!);
        var path = includePath && r.Path != null ? ExportAll(c.Session, windowId, view, r.Path) : null;
        return new { target, matchCount = r.MatchCount, uniquenessScope = r.UniquenessScope, path };
    }
    /// <summary>Error data from the worker can carry nodes (ambiguous candidates); they become session references.</summary>
    private GuiError ExportError(ControlSession s, string windowId, string view, GuiError error)
    {
        if (error.Code != "AmbiguousTarget" || error.Data == null) return error;
        var data = GuiProtocol.Element(error.Data);
        if (!data.TryGetProperty("candidates", out var list)) return error;
        var nodes = list.Deserialize<ObservationNode[]>(GuiProtocol.Json)!;
        var parents = data.TryGetProperty("parentNames", out var names) ? names.Deserialize<string[]>()! : [];
        var candidates = nodes.Select((n, i) => { var e = Export(s, windowId, view, n); return new { elementId = e.ElementId, snapshotToken = e.SnapshotToken, e.AutomationId, e.Name, e.ControlType, parentName = i < parents.Length ? parents[i] : null }; }).ToArray();
        return error with { Data = new { matchCount = data.GetProperty("matchCount").GetInt32(), candidates } };
    }
    private static GuiException Throw(GuiError error) => new(error.Code, error.Message, error.Data) { NoEffect = true };
    // ------------------------------------------------------------------ operations
    private object Perform(Ctx c)
    {
        var s = c.Session; var a = c.A; var op = c.Request.Operation;
        switch (op)
        {
            case "windows":
                var listed = ListWindows(s);
                var kind = GuiProtocol.OptionalText(a, "kind");
                var includePopups = GuiProtocol.Flag(a, "includePopups") || kind == "popup";
                return new { windows = listed.Where(w => (kind == null || w.Kind == kind) && (includePopups || w.Kind != "popup")).ToArray() };
            case "find-window":
                return new { window = WindowQuery.Find(ListWindows(s), a, false) };
            case "wait-window":
            {
                var poll = GuiProtocol.OptionalInt(a, "pollMilliseconds") ?? 100; var polls = 0;
                // absent: true waits until no in-scope window matches (a dialog closed, an app exited).
                var absent = GuiProtocol.Flag(a, "absent");
                while (true)
                {
                    polls++;
                    if (absent)
                    {
                        var remaining = ListWindows(s).Where(x => WindowQuery.Matches(x, a)).ToArray();
                        if (remaining.Length == 0) return new { satisfied = true, polls };
                        if (c.RemainingMilliseconds < poll + 50) return new { satisfied = false, polls, windows = remaining };
                    }
                    else
                    {
                        var found = WindowQuery.Find(ListWindows(s), a, true);
                        if (found != null) return new { satisfied = true, window = found, polls };
                        if (c.RemainingMilliseconds < poll + 50) return new { satisfied = false, polls };
                    }
                    Task.Delay(poll, c.Cancellation).GetAwaiter().GetResult();
                }
            }
            case "window-state":
            {
                var w = Window(c); var state = c.T("state");
                c.Effect.Started = true;
                try { Uia(c, new UiaCommand(w, "window-state", a)); }
                catch (GuiException e) when (e.Code == "PatternNotSupported") { desktop.SetState(w, state); }
                return new { windowId = c.T("windowId"), state };
            }
            case "focus":
            {
                var w = Window(c); c.Effect.Started = true;
                if (!desktop.Focus(w)) throw new GuiException("FocusDenied");
                return new { windowId = c.T("windowId"), foreground = true };
            }
            case "close-window": return CloseWindow(c);
            case "launch": return Launch(c);
            case "open": return Open(c);
            case "terminate-owned":
            {
                c.Effect.Started = true;
                if (!Applications.Terminate(s.Info.ControlSessionId, a.GetProperty("processIds").EnumerateArray().Select(x => x.GetString()!).ToArray())) throw new GuiException("ApplicationStopFailed");
                return new { applicationsStopped = true };
            }
            case "inspect":
            {
                var w = Window(c); var windowId = c.T("windowId");
                var result = Uia(c, Command(c, w));
                var nodes = ExportAll(s, windowId, View(c), result.Nodes);
                var containers = result.VirtualizedContainers?.Select(n => Export(s, windowId, View(c), n).ElementId).ToArray();
                var format = GuiProtocol.OptionalText(a, "format") ?? "compact";
                object tree = format == "json" ? nodes : CompactTree.Format(nodes, GuiProtocol.Flag(a, "includeBounds"));
                return new { windowId, format, tree, nodeCount = nodes.Length, complete = result.Complete, truncationReason = result.TruncationReason, virtualizedContainers = containers };
            }
            case "find-element":
            {
                var w = Window(c); var windowId = c.T("windowId"); var view = View(c);
                var command = Command(c, w);
                command = GuiProtocol.Has(a, "selectors") ? command with { Selectors = a.GetProperty("selectors").Deserialize<ElementSelector[]>(GuiProtocol.Json) } : command with { Selector = a.GetProperty("selector").Deserialize<ElementSelector>(GuiProtocol.Json) };
                var result = Uia(c, command); var includePath = GuiProtocol.Flag(a, "includePath");
                var containers = result.VirtualizedContainers?.Select(n => Export(s, windowId, view, n).ElementId).ToArray();
                if (command.Selectors != null) return new { results = result.Results!.Select(r => ExportResult(c, r, windowId, view, includePath)).ToArray(), virtualizedContainers = containers };
                var single = result.Results![0];
                if (single.Error != null) throw Throw(ExportError(s, windowId, view, single.Error));
                var target = Export(s, windowId, view, single.Target!);
                return new { target, matchCount = single.MatchCount, uniquenessScope = single.UniquenessScope, path = includePath && single.Path != null ? ExportAll(s, windowId, view, single.Path) : null, virtualizedContainers = containers };
            }
            case "capabilities":
            {
                var w = Window(c); var windowId = c.T("windowId"); var view = View(c);
                var command = Command(c, w);
                if (GuiProtocol.Has(a, "elements"))
                {
                    var locators = a.GetProperty("elements").EnumerateArray().Select(e => { try { return Locate(c, source: e); } catch (GuiException) { return new ElementLocator("0", "stale", view); } }).ToArray();
                    command = command with { Elements = locators };
                }
                else if (GuiProtocol.Has(a, "elementId")) { var locator = Locate(c); command = command with { Target = locator, TreeView = locator.TreeView }; view = locator.TreeView; }
                else command = command with { Selector = a.GetProperty("selector").Deserialize<ElementSelector>(GuiProtocol.Json) };
                var result = Uia(c, command);
                var descriptors = GuiProtocol.Flag(a, "includeDescriptors") ? Operations.All : null;
                if (command.Elements != null) return new { results = result.Results!.Select(r => ExportResult(c, r, windowId, view, false)).ToArray(), operationDescriptors = descriptors };
                return new { target = Export(s, windowId, view, result.Target!), matchCount = result.MatchCount == 0 ? (int?)null : result.MatchCount, uniquenessScope = result.UniquenessScope, operationDescriptors = descriptors };
            }
            case "wait-condition": return Wait(c, Window(c));
            case "find-item":
            case "read-grid":
            case "read-text":
            case "generate-selector":
            {
                var w = Window(c); var windowId = c.T("windowId"); var locator = Locate(c);
                var result = Uia(c, Command(c, w) with { Target = locator, TreeView = locator.TreeView });
                var target = result.Target == null ? null : Export(s, windowId, locator.TreeView, result.Target);
                if (op == "find-item") return new { target };
                var data = result.Data?.Deserialize<JsonObject>() ?? new JsonObject();
                data["target"] = JsonSerializer.SerializeToNode(target, GuiProtocol.Json);
                return data;
            }
            case "capture":
            {
                var w = Window(c); PixelRect? region = null;
                if (GuiProtocol.Has(a, "region")) region = a.GetProperty("region").Deserialize<PixelRect>(GuiProtocol.Json);
                else if (GuiProtocol.Has(a, "elementId"))
                {
                    var locator = Locate(c);
                    var target = Uia(c, new UiaCommand(w, "capabilities", a) { Target = locator, TreeView = locator.TreeView }).Target;
                    var b = target?.Bounds ?? throw new GuiException("ElementNotVisible", "ElementHasNoBounds") { NoEffect = true };
                    int x = Math.Max(0, b.X), y = Math.Max(0, b.Y);
                    region = new(x, y, Math.Min(w.Bounds.Width, b.X + b.Width) - x, Math.Min(w.Bounds.Height, b.Y + b.Height) - y);
                    if (region.Width < 1 || region.Height < 1) throw new GuiException("ElementNotVisible", "ElementOutsideClientArea") { NoEffect = true };
                }
                var scale = Has(a, "scale") ? a.GetProperty("scale").GetDouble() : 1;
                return Captures.Take(c.T("windowId"), w, region, scale, c.Cancellation);
            }
            case "clipboard-get": return new { text = desktop.ClipboardText() };
            case "clipboard-set": c.Effect.Started = true; desktop.SetClipboardText(c.T("text")); return new { clipboardSet = true };
            case "click": case "type-text": case "key": case "chord": case "move": case "wheel": case "drag":
                return Input(c);
            default:
                if (Operations.ElementMutation(op)) return ElementMutation(c);
                throw new GuiException("UnsupportedOperation") { NoEffect = true };
        }
    }
    private static bool Has(JsonElement a, string name) => GuiProtocol.Has(a, name);
    /// <summary>Asks the window to close and waits (waitMilliseconds, default 2000) until it is gone. When it stays open,
    /// new windows of the same process are reported: usually a "save changes?" prompt that needs an answer.</summary>
    private object CloseWindow(Ctx c)
    {
        var w = Window(c); var windowId = c.T("windowId");
        var before = desktop.Windows().Select(x => x.Handle).ToHashSet();
        c.Effect.Started = true; desktop.Close(w);
        var wait = Math.Min(GuiProtocol.OptionalInt(c.A, "waitMilliseconds") ?? 2000, Math.Max(0, c.RemainingMilliseconds - 100));
        var limit = Stopwatch.GetTimestamp() + (long)(wait * (double)Stopwatch.Frequency / 1000);
        bool closed;
        while (true)
        {
            try { closed = !WindowQuery.Same(w, desktop.Identify(w.Handle)); } catch (GuiException) { closed = true; }
            if (closed || Stopwatch.GetTimestamp() >= limit) break;
            Task.Delay(50, c.Cancellation).GetAwaiter().GetResult();
        }
        var appeared = desktop.Windows().Where(x => !before.Contains(x.Handle) && x.Process == w.Process).Select(x => Publish(c.Session, x)).ToArray();
        var processExited = closed && !Processes.Alive(w.Process);
        return new { windowId, closeRequested = true, closed, processExited, newWindows = appeared.Length > 0 ? appeared : null,
            hint = !closed && appeared.Length > 0 ? "The window is still open and showed a new window (likely a confirmation prompt). Answer it, then close again." : null };
    }
    private object Launch(Ctx c)
    {
        var s = c.Session; var a = c.A;
        var args = Has(a, "arguments") ? a.GetProperty("arguments").EnumerateArray().Select(x => x.GetString()!).ToArray() : [];
        var owned = Applications.Launch(s.Info.ControlSessionId, c.T("executable"), args, GuiProtocol.OptionalText(a, "lifetime") ?? "session",
            GuiProtocol.OptionalText(a, "workingDirectory"), GuiProtocol.OptionalText(a, "verb") ?? "open", () => c.Effect.Started = true);
        object? window = null; bool? satisfied = null;
        if (Has(a, "waitWindow"))
        {
            var wait = a.GetProperty("waitWindow");
            (window, satisfied) = WaitForWindow(c, wait, w => w.Process == owned.Identity || Applications.Children(s.Info.ControlSessionId).Contains(w.Process), null);
        }
        return new { processId = owned.ProcessId, pid = owned.Identity.Pid, imagePath = owned.Identity.ImagePath, lifetime = owned.Lifetime, window, waitSatisfied = satisfied };
    }
    private object Open(Ctx c)
    {
        var s = c.Session; var a = c.A;
        var before = desktop.Windows().Select(w => w.Handle).ToHashSet();
        var args = Has(a, "arguments") ? a.GetProperty("arguments").EnumerateArray().Select(x => x.GetString()!).ToArray() : [];
        var process = GuiApplications.Open(c.T("target"), args, GuiProtocol.OptionalText(a, "verb") ?? "open", GuiProtocol.OptionalText(a, "workingDirectory"), () => c.Effect.Started = true);
        if (process != null) s.OpenedProcesses[process.Pid] = process;
        var wait = Has(a, "waitWindow") ? a.GetProperty("waitWindow") : JsonSerializer.SerializeToElement(new { timeoutMilliseconds = 2000 });
        var (window, satisfied) = WaitForWindow(c, wait, w => !before.Contains(w.Handle) || process != null && w.Process == process, before);
        return new { pid = process?.Pid, processName = process == null ? null : Processes.Name(process), window, waitSatisfied = satisfied };
    }
    /// <summary>Polls for a new top-level window matching waitWindow. New windows become part of the session scope.</summary>
    private (PublicWindow? Window, bool Satisfied) WaitForWindow(Ctx c, JsonElement wait, Func<DesktopWindow, bool> candidate, HashSet<long>? before)
    {
        var s = c.Session;
        var limit = Stopwatch.GetTimestamp() + (long)(Math.Min(GuiProtocol.OptionalInt(wait, "timeoutMilliseconds") ?? 10000, Math.Max(0, c.RemainingMilliseconds - 100)) * (double)Stopwatch.Frequency / 1000);
        string? title = GuiProtocol.OptionalText(wait, "title"), contains = GuiProtocol.OptionalText(wait, "titleContains"), className = GuiProtocol.OptionalText(wait, "className");
        var conditioned = title != null || contains != null || className != null;
        while (true)
        {
            var all = desktop.Windows();
            var match = all.Where(w => candidate(w) || conditioned && before != null).Where(w => w.Kind != "popup")
                .Where(w => (title == null || w.Title == title) && (contains == null || w.Title.Contains(contains, StringComparison.Ordinal)) && (className == null || w.ClassName == className))
                .OrderBy(w => w.Kind == "main" ? 0 : 1).FirstOrDefault();
            if (match != null)
            {
                if (before != null)
                {
                    // The matched window and other new windows of the same process (frame host and content) join the scope.
                    s.OpenedWindows.TryAdd(match.Handle, before.Contains(match.Handle) ? "existing" : "opened");
                    foreach (var w in all.Where(w => !before.Contains(w.Handle) && w.Process == match.Process && w.Kind != "popup")) s.OpenedWindows.TryAdd(w.Handle, "opened");
                }
                return (Publish(s, match), true);
            }
            if (Stopwatch.GetTimestamp() >= limit) return (null, false);
            Task.Delay(100, c.Cancellation).GetAwaiter().GetResult();
        }
    }
    // ------------------------------------------------------------------ waiting
    private sealed record Condition(ElementSelector Selector, string Name, string? Expected);
    private static Condition ReadCondition(JsonElement e) => new(e.GetProperty("selector").Deserialize<ElementSelector>(GuiProtocol.Json)!, e.GetProperty("condition").GetString()!, GuiProtocol.OptionalText(e, "expected"));
    /// <summary>true/false = decided; null = keep polling. Throws for errors that polling cannot fix.</summary>
    private static bool? Evaluate(Condition condition, SelectorResult r, out string? lastError)
    {
        lastError = r.Error?.Code;
        if (r.Error != null)
        {
            switch (r.Error.Code)
            {
                case "ElementNotFound": return condition.Name == "absent";
                case "SearchIncomplete" when r.Error.Message == "NotFoundOutsideVirtualized":
                    if (condition.Name == "absent") throw Throw(r.Error);
                    return false;
                case "AmbiguousTarget": case "SearchIncomplete": case "InvalidSelector": throw Throw(r.Error);
                default: return null; // StaleElement, provider read failures: try again on the next poll.
            }
        }
        var t = r.Target!; var e = condition.Expected;
        return condition.Name switch
        {
            "exists" => true,
            "absent" => false,
            "enabled" => t.Enabled,
            "disabled" => !t.Enabled,
            "value-equals" => t.Value.Available && t.Value.Text == e,
            "value-not-equals" => t.Value.Available && t.Value.Text != e,
            "text-contains" => t.Value.Available && t.Value.Text?.Contains(e!, StringComparison.Ordinal) == true,
            "name-equals" => t.Name == e,
            "name-contains" => t.Name.Contains(e!, StringComparison.Ordinal),
            "toggle-equals" => t.ToggleState == e,
            "selected-equals" => t.Selected != null && (t.Selected.Value ? "true" : "false") == e,
            "expanded-equals" => t.ExpandState == e,
            "onscreen" => !t.Offscreen,
            "offscreen" => t.Offscreen,
            _ => false
        };
    }
    private object Wait(Ctx c, DesktopWindow w, Condition[]? conditions = null, int? timeout = null, int? pollOverride = null)
    {
        var a = c.A; var windowId = c.T("windowId"); var view = View(c);
        conditions ??= Has(a, "anyOf") ? a.GetProperty("anyOf").EnumerateArray().Select(ReadCondition).ToArray() : [ReadCondition(a)];
        var poll = pollOverride ?? GuiProtocol.OptionalInt(a, "pollMilliseconds") ?? 100;
        var limit = Stopwatch.GetTimestamp() + (long)(Math.Min(timeout ?? int.MaxValue, Math.Max(0, c.RemainingMilliseconds - 60)) * (double)Stopwatch.Frequency / 1000);
        var command = (c.Request.Operation == "wait-condition" ? Command(c, w, "wait-condition") : new UiaCommand(w, "wait-condition", a)) with { Selectors = conditions.Select(x => x.Selector).ToArray() };
        int polls = 0; string? lastError = null; SelectorResult[]? last = null;
        while (true)
        {
            polls++;
            try
            {
                last = Uia(c, command).Results!;
                for (int i = 0; i < conditions.Length; i++)
                {
                    var decided = Evaluate(conditions[i], last[i], out var error); lastError = error ?? lastError;
                    if (decided == true)
                    {
                        var target = last[i].Target == null ? null : Export(c.Session, windowId, view, last[i].Target!);
                        return new { satisfied = true, matchedIndex = conditions.Length > 1 ? i : (int?)null, target, polls };
                    }
                }
            }
            catch (GuiException e) when (e.Code is "StaleElement" or "ProviderError" && !e.Message.Contains("Window", StringComparison.Ordinal)) { lastError = e.Code; }
            if (Stopwatch.GetTimestamp() + (long)(poll * (double)Stopwatch.Frequency / 1000) >= limit)
            {
                var observed = last?.Select(r => r.Target == null ? null : Export(c.Session, windowId, view, r.Target)).ToArray();
                return new { satisfied = false, polls, lastError, target = observed?.FirstOrDefault(), targets = conditions.Length > 1 ? observed : null };
            }
            Task.Delay(poll, c.Cancellation).GetAwaiter().GetResult();
        }
    }
    // ------------------------------------------------------------------ mutations
    private object ElementMutation(Ctx c)
    {
        var w = Window(c); var windowId = c.T("windowId"); var locator = Locate(c);
        var before = AfterSnapshot(c);
        lock (c.Session.Mutation) { c.Cancellation.ThrowIfCancellationRequested(); sessions.Check(c.Session, true); }
        // The worker reports whether it reached the provider call; until then nothing has happened.
        c.Effect.Started = true;
        var result = Uia(c, new UiaCommand(w, c.Request.Operation, c.A) { Target = locator, TreeView = locator.TreeView });
        var data = new JsonObject
        {
            ["target"] = JsonSerializer.SerializeToNode(Export(c.Session, windowId, locator.TreeView, result.Target!), GuiProtocol.Json),
            ["dispatchKind"] = result.DispatchKind
        };
        if (result.Data is { } extra && extra.TryGetProperty("pendingModal", out var pending) && pending.GetBoolean())
        {
            // The action is waiting on a modal window it opened. The session stays usable: answer the window.
            var modal = new List<PublicWindow>();
            foreach (var h in extra.GetProperty("modalWindowHandles").EnumerateArray().Select(x => x.GetInt64()))
            {
                // In scope for this session, but never closed by the session itself.
                try { modal.Add(Publish(c.Session, desktop.Identify(h))); c.Session.OpenedWindows.TryAdd(h, "existing"); } catch (GuiException) { }
            }
            data["dispatchState"] = "pending-modal";
            data["modalWindows"] = JsonSerializer.SerializeToNode(modal, GuiProtocol.Json);
            data["hint"] = "The action opened a modal window and has not returned yet. Answer that window (find its button and invoke it, or close-window); the action finishes when it closes.";
            After(c, w, data, before, skipExpect: true);
            return data;
        }
        After(c, w, data, before);
        return data;
    }
    private HashSet<long>? AfterSnapshot(Ctx c) =>
        Has(c.A, "observeAfter") && GuiProtocol.Flag(c.A.GetProperty("observeAfter"), "includeNewWindows") ? desktop.Windows().Select(w => w.Handle).ToHashSet() : null;
    /// <summary>expect and observeAfter: their failures never change the outcome of the dispatched operation.</summary>
    private void After(Ctx c, DesktopWindow w, JsonObject data, HashSet<long>? before, bool skipExpect = false)
    {
        var a = c.A;
        if (Has(a, "expect") && !skipExpect)
        {
            try
            {
                var e = a.GetProperty("expect");
                data["expect"] = JsonSerializer.SerializeToNode(Wait(c, w, [ReadCondition(e)], GuiProtocol.OptionalInt(e, "timeoutMilliseconds") ?? 2000, GuiProtocol.OptionalInt(e, "pollMilliseconds") ?? 50), GuiProtocol.Json);
            }
            catch (Exception e) { data["afterError"] = e is GuiException g ? g.Code : "ExpectFailed"; }
        }
        if (Has(a, "observeAfter"))
        {
            try
            {
                var o = a.GetProperty("observeAfter"); var windowId = c.T("windowId");
                var settle = GuiProtocol.OptionalInt(o, "settleMilliseconds") ?? 100; if (settle > 0) Task.Delay(settle, c.Cancellation).GetAwaiter().GetResult();
                var current = desktop.Identify(w.Handle);
                var root = Has(o, "rootElementId") ? Locate(c, "rootElementId", "rootSnapshotToken", o) : null;
                var arguments = JsonSerializer.SerializeToElement(new { maxDepth = GuiProtocol.OptionalInt(o, "maxDepth") ?? 6 });
                var result = Uia(c, new UiaCommand(current, "inspect", arguments) { SearchRoot = root, TreeView = root?.TreeView ?? "control" });
                data["after"] = new JsonObject { ["tree"] = CompactTree.Format(ExportAll(c.Session, windowId, root?.TreeView ?? "control", result.Nodes), false) };
                if (before != null)
                {
                    var fresh = new Scope(this, c.Session).Filter(desktop.Windows()).Where(x => !before.Contains(x.Handle)).Select(x => Publish(c.Session, x)).ToArray();
                    data["after"]!["newWindows"] = JsonSerializer.SerializeToNode(fresh, GuiProtocol.Json);
                }
            }
            catch (Exception e) { data["afterError"] = e is GuiException g ? g.Code : "ObserveFailed"; }
        }
    }
    /// <summary>Top two levels of the window and the in-scope windows, attached to "not found"-type errors.</summary>
    private JsonNode? ErrorContext(ControlSession s, GuiRequest r)
    {
        try
        {
            var windowId = GuiProtocol.OptionalText(r.Arguments, "windowId"); if (windowId == null) return null;
            var known = s.Windows.Get(windowId); var w = desktop.Identify(known.Handle);
            using var limit = new CancellationTokenSource(1500);
            var result = desktop.Automation(s.Info.ControlSessionId, new UiaCommand(w, "inspect", JsonSerializer.SerializeToElement(new { maxDepth = 2 })), limit.Token);
            var windows = ListWindows(s).Select(x => new { x.WindowId, x.Title, x.ClassName, x.Kind }).ToArray();
            return new JsonObject { ["tree"] = CompactTree.Format(ExportAll(s, windowId, "control", result.Nodes), false), ["windows"] = JsonSerializer.SerializeToNode(windows, GuiProtocol.Json) };
        }
        catch { return null; }
    }
    // ------------------------------------------------------------------ real input
    private void Guard(Ctx c, DesktopWindow? foreground = null)
    {
        lock (c.Session.Mutation)
        {
            c.Cancellation.ThrowIfCancellationRequested(); sessions.Check(c.Session, true);
            if (!desktop.Valid) throw new GuiException("UnsupportedDesktop") { NoEffect = true };
            Interaction?.Check(c.Session);
            if (foreground != null && !desktop.Identify(foreground.Handle).Foreground) throw new GuiException("ForegroundMismatch", "Focus the window first (focus operation)") { NoEffect = !c.Effect.Started };
        }
    }
    private void Sent(Ctx c, int count = 1) { c.Effect.Started = true; c.Effect.Known = true; c.Effect.InputEvents += count; }
    private (int X, int Y) Point(Ctx c, DesktopWindow w, JsonElement p)
    {
        int x = p.GetProperty("x").GetInt32(), y = p.GetProperty("y").GetInt32();
        if (GuiProtocol.OptionalText(p, "captureId") is { } capture) (x, y) = Captures.Map(c.T("windowId"), capture, x, y, w);
        else if (!new PixelRect(0, 0, w.Bounds.Width, w.Bounds.Height).Contains(x, y)) throw new GuiException("InvalidPosition", "OutsideClientArea") { NoEffect = true };
        return (w.Bounds.X + x, w.Bounds.Y + y);
    }
    private object Input(Ctx c)
    {
        var a = c.A; var sid = c.Session.Info.ControlSessionId; var op = c.Request.Operation;
        var w = Window(c);
        if (w.Minimized) throw new GuiException("WindowMinimized") { NoEffect = true };
        var before = AfterSnapshot(c);
        var data = new JsonObject { ["windowId"] = c.T("windowId") };
        switch (op)
        {
            case "click":
            {
                int x, y;
                if (Has(a, "elementId"))
                {
                    var locator = Locate(c);
                    // Stability: the element must stay put between two reads 50 ms apart (up to 500 ms).
                    PixelRect? Read() => Uia(c, new UiaCommand(w, "capabilities", a) { Target = locator, TreeView = locator.TreeView }).Target?.Bounds;
                    var bounds = Read(); var stable = Stopwatch.StartNew();
                    while (true)
                    {
                        Task.Delay(50, c.Cancellation).GetAwaiter().GetResult(); var again = Read();
                        if (again == bounds) break;
                        bounds = again; if (stable.ElapsedMilliseconds > 500) throw new GuiException("ElementNotStable") { NoEffect = true };
                    }
                    var b = bounds ?? throw new GuiException("ElementNotVisible", "ElementHasNoBounds") { NoEffect = true };
                    x = w.Bounds.X + b.X + b.Width / 2; y = w.Bounds.Y + b.Y + b.Height / 2;
                    var hit = Uia(c, new UiaCommand(w, "hit-test", a) { Target = locator, HitPoint = new(x, y, 0, 0) }).Data!.Value;
                    if (!hit.GetProperty("inside").GetBoolean()) throw new GuiException("TargetObscured", "Another element covers the target", new { blockedBy = hit.GetProperty("blockedBy") }) { NoEffect = true };
                    data["target"] = c.T("elementId");
                }
                else (x, y) = Point(c, w, a.GetProperty("position"));
                Guard(c); Sent(c); input.Move(x, y);
                if (!desktop.PointerMatches(w)) throw new GuiException("PointerTargetMismatch", "The pointer is over another window");
                var keys = new List<(string, int)>();
                if (Has(a, "modifiers")) foreach (var m in a.GetProperty("modifiers").EnumerateArray()) keys.Add(("key", GuiProtocol.Key(m.GetString()!)));
                keys.Add(("button", GuiProtocol.Button(a)));
                var count = GuiProtocol.OptionalInt(a, "count") ?? 1;
                Guard(c); input.Press(sid, keys, () => Sent(c, keys.Count * 2 + (count - 1) * 2), count);
                data["screen"] = new JsonObject { ["x"] = x, ["y"] = y };
                break;
            }
            case "type-text":
                Guard(c, w);
                var sent = input.Type(c.T("text"), () => Guard(c, w), () => { c.Effect.Started = true; c.Effect.Known = true; });
                c.Effect.InputEvents += sent; break;
            case "key":
            {
                var key = GuiProtocol.Key(c.T("key")); var action = GuiProtocol.OptionalText(a, "action") ?? "press";
                if (action == "up") { lock (c.Session.Mutation) if (input.Up(sid, "key", key)) Sent(c); }
                else { Guard(c, w); if (action == "down") input.Down(sid, "key", key, () => Sent(c)); else input.Press(sid, [("key", key)], () => Sent(c, 2)); }
                break;
            }
            case "chord":
            {
                var keys = a.GetProperty("keys").EnumerateArray().Select(x => ("key", GuiProtocol.Key(x.GetString()!))).ToList();
                Guard(c, w); input.Press(sid, keys, () => Sent(c, keys.Count * 2)); break;
            }
            case "move":
            {
                var (x, y) = Point(c, w, a.GetProperty("position")); Guard(c); Sent(c); input.Move(x, y); break;
            }
            case "wheel":
            {
                var (x, y) = Point(c, w, a.GetProperty("position")); Guard(c); Sent(c); input.Move(x, y);
                if (!desktop.PointerMatches(w)) throw new GuiException("PointerTargetMismatch");
                Guard(c); Sent(c); input.Wheel(a.GetProperty("delta").GetInt32()); break;
            }
            case "drag":
            {
                var capture = GuiProtocol.OptionalText(a, "captureId");
                JsonElement P(string name) { var p = a.GetProperty(name); return capture == null ? p : JsonSerializer.SerializeToElement(new { x = p.GetProperty("x").GetInt32(), y = p.GetProperty("y").GetInt32(), captureId = capture }); }
                var start = Point(c, w, P("start")); var end = Point(c, w, P("end"));
                var steps = GuiProtocol.OptionalInt(a, "steps") ?? 10; var button = GuiProtocol.Button(a);
                Guard(c); Sent(c); input.Move(start.X, start.Y);
                if (!desktop.PointerMatches(w)) throw new GuiException("PointerTargetMismatch");
                Guard(c); input.Down(sid, "button", button, () => Sent(c));
                try
                {
                    for (int i = 1; i <= steps; i++)
                    {
                        Guard(c); Sent(c);
                        input.Move((int)Math.Round(start.X + ((double)end.X - start.X) * i / steps), (int)Math.Round(start.Y + ((double)end.Y - start.Y) * i / steps));
                    }
                }
                finally { lock (c.Session.Mutation) if (input.Up(sid, "button", button)) Sent(c); }
                break;
            }
        }
        data["inputEventsSent"] = c.Effect.InputEvents;
        After(c, w, data, before);
        return data;
    }
}

using System.Diagnostics;
using System.Text.Json;

namespace DesktopTestDriver;

public sealed record AgentCommand(GuiRequest Request, int RemainingMilliseconds);
public sealed class GuiRequestHandler
{
    private readonly IGuiDesktop desktop;
    private readonly StateStore store;
    private readonly ControlSessions sessions;
    private readonly RequestJournal journal;
    private readonly WindowRegistry windows;
    public CaptureStore Captures { get; }
    private readonly GuiInput input;
    private readonly GuiApplications applications;
    private readonly int maxStop;
    private readonly string machineUsage;
    public InteractionGuard? Interaction { get; init; }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> windowOwners = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Session, string Window, ObservationNode Node)> observed = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,string> observedAliases=new();
    public GuiRequestHandler(StateStore store, IGuiDesktop desktop, ControlSessions sessions, GuiInput input, int maxStop = 3000, string machineUsage = "shared")
    {
        this.store = store; this.desktop = desktop; this.sessions = sessions; this.input = input; this.maxStop = maxStop; this.machineUsage = machineUsage;
        journal = new(store); journal.Recover(false); windows = new(desktop); Captures = new(store, desktop, windows); applications = new(store);
    }
    public GuiResponse Result(string session, string request) => journal.Result(session, request);
    public async Task<GuiResponse> Execute(AgentCommand command)
    {
        // Re-parse at the trust boundary, including all argument validation.
        var r = GuiProtocol.Parse(GuiProtocol.Encode(command.Request));
        var s = sessions.Get(r.ControlSessionId); var changing = !GuiProtocol.ReadOnly(r.Operation);
        if (s.Info.Mode == "observe" && changing) return GuiResponse.Reject("ReadOnlySession", r);
        if (r.ApiVersion == 2 && s.Info.ApiVersion != 2) return GuiResponse.Reject("SessionApiVersionMismatch", r);
        if (changing && r.Operation != "release-input" && !AutomationOperations.Contains(r.Operation) && (s.Info.InteractionPolicy == "background-only" || machineUsage == "shared" && s.Info.InteractionPolicy != "foreground-exclusive"))
            return GuiResponse.Reject("BackgroundQualificationRequired", r);
        (JournalEntry Entry, bool New) accepted;
        CancellationToken permit;
        lock (journal.Gate(r.ControlSessionId, r.RequestId))
        {
            var existing = journal.Find(r.ControlSessionId, r.RequestId);
            if (existing != null)
            {
                if (existing.Fingerprint != GuiProtocol.Fingerprint(r)) throw new GuiException("DuplicateRequestMismatch");
                return existing.Response;
            }
            lock (s.Mutation)
            {
                // Admission and permit capture are atomic with close/release, including queued commands.
                if (r.Operation != "release-input")
                {
                    try { sessions.Check(s, changing); }
                    catch (GuiException e) { return GuiResponse.Reject(e.Code, r); }
                }
                if (changing) EvidenceBudget.Admit(store, store.Exists("evidence-policy.json") ? store.Read<EvidencePolicy>("evidence-policy.json") : new EvidencePolicy());
                accepted = journal.Accept(r, Deadline.Start(Math.Clamp(command.RemainingMilliseconds, 0, r.TimeoutMilliseconds)));
                permit = !changing && s.Info.State == "blocked" ? CancellationToken.None : s.Permit.Token;
                if (r.Operation != "release-input") { s.Touch(); sessions.Save(s); }
            }
        }
        int sent = 0; bool acquired = false; bool effect = false;
        GuiResponse response;
        try
        {
            if (accepted.Entry.Deadline.Remaining <= 0) throw new GuiException("RequestTimeout");
            if (r.Operation == "release-input")
            {
                var released = await Release(s, false, "ExplicitRelease");
                response = new(released ? "completed" : "failed", r.RequestId, r.ControlSessionId, new { inputReleased = released }, released ? null : new("InputReleaseFailed", "InputReleaseFailed"), InputReleased: released);
            }
            else
            {
                using var timeout = new CancellationTokenSource(accepted.Entry.Deadline.Remaining);
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, permit);
                await s.Queue.WaitAsync(cancellation.Token); acquired = true;
                sessions.Check(s, changing);
                if (!desktop.Valid) throw new GuiException("UnsupportedDesktop");
                lock (s.Mutation) { sessions.Check(s, changing); }
                journal.Save(accepted.Entry with { Phase = "executing" });
                object data = await Task.Run(() => Perform(r, s, cancellation.Token, () => { sent++; effect = true; }, () => { journal.Save(accepted.Entry with { Phase = "dispatching" }); effect = true; }), CancellationToken.None);
                cancellation.Token.ThrowIfCancellationRequested();
                response = new("completed", r.RequestId, r.ControlSessionId, data, InputEventsSent: sent, InputReleased: input.Empty(s.Info.ControlSessionId));
            }
        }
        catch (Exception e)
        {
            bool? released = null;
            try { lock (s.Mutation) { released = s.Info.Mode == "observe" ? input.Empty(s.Info.ControlSessionId) : input.Release(s.Info.ControlSessionId); if (released != true) { s.Info = s.Info with { State = "blocked", InputReleased = released, StopReason = "InputReleaseFailed" }; sessions.Save(s); } } } catch { }
            string code = e is GuiException g ? g.Code : e is OperationCanceledException ? "RequestTimeout" : "OperationFailed";
            response = new(effect && r.ApiVersion == 2 ? "completionUnknown" : effect ? "failed" : "rejected", r.RequestId, r.ControlSessionId, Error: new(code, e is GuiException ? e.Message : code), InputEventsSent: sent, InputReleased: released);
        }
        finally { if (acquired) s.Queue.Release(); }
        if (response.Status == "completionUnknown" && changing)
        {
            sessions.Pause(r.ControlSessionId, "UnconfirmedEffect");
            lock (s.Mutation) { s.Info = s.Info with { PendingUnknownRequestIds = s.Info.PendingUnknownRequestIds.Append(r.RequestId).Distinct().ToArray() }; sessions.Save(s); }
        }
        response = response with { ApiVersion = r.ApiVersion, Operation = r.Operation, MachineId = s.Info.MachineId };
        if (r.Operation != "capture")
        {
            var data = response.Data == null ? new Dictionary<string, object?>() : GuiProtocol.Decode<Dictionary<string, object?>>(GuiProtocol.Encode(response.Data));
            if (r.ApiVersion == 2)
            {
                data["backend"] = AutomationOperations.Contains(r.Operation) ? "windows-uia" : "windows-input";
                data["effectState"] = response.Status == "completionUnknown" ? "unknown" : effect ? "dispatched" : "none";
                data["foregroundChangeObserved"] = Interaction?.ForegroundChangeObserved(s);
                data["driverOutcome"] = response.Status;
                data["interactionPolicy"] = s.Info.InteractionPolicy;
            }
            data["inputEventsSent"] = response.InputEventsSent; data["inputReleased"] = response.InputReleased;
            response = response with { Data = data };
        }
        journal.Finish(r, response); return response;
    }
    public async Task<bool> Release(ControlSession s, bool close, string reason)
    {
        var started = Stopwatch.GetTimestamp();
        // Remember close intent even if a stuck native call prevents taking Mutation.
        if (close) { lock (s) { s.CloseReason = reason; s.CloseRequested = true; } }
        s.Permit.Cancel();
        if (!await s.Releasing.WaitAsync(maxStop)) return false;
        try { return await ReleaseCore(s, reason, started); }
        finally { s.Releasing.Release(); }
    }
    private async Task<bool> ReleaseCore(ControlSession s, string reason, long started)
    {
        CancellationTokenSource old;
        // Revoke before waiting for the native dispatch gate, even when its owner is stuck.
        s.Permit.Cancel();
        if (!Monitor.TryEnter(s.Mutation, Math.Max(0, maxStop - (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds)))
        {
            s.Info = s.Info with { State = "blocked", InputReleased = null, StopReason = "DispatchStopUnconfirmed" };
            sessions.Save(s); store.Write("worker-stop-needed.json", new { sessionId = s.Info.ControlSessionId, reason }); return false;
        }
        try
        {
            if (s.Info.State is "closed" or "expired") return s.Info.InputReleased == true && s.Info.ApplicationsStopped == true;
            old = s.Permit; s.Info = s.Info with { State = "closing", StopReason = reason }; old.Cancel(); sessions.Save(s);
        }
        finally { Monitor.Exit(s.Mutation); }
        if (!await s.Queue.WaitAsync(Math.Max(0, maxStop - (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds)))
        {
            s.Info = s.Info with { State = "blocked", InputReleased = null, ApplicationsStopped = null, StopReason = "WorkerStopUnconfirmed" }; sessions.Save(s);
            store.Write("worker-stop-needed.json", new { sessionId = s.Info.ControlSessionId, reason });
            return false;
        }
        try
        {
            lock (s.Mutation)
            {
                Interaction?.Release(s.Info.ControlSessionId);
                if (desktop is IAutomationDesktop automation) automation.ReleaseAutomation(s.Info.ControlSessionId);
                var released = s.Info.Mode == "observe" ? input.Empty(s.Info.ControlSessionId) : input.Release(s.Info.ControlSessionId);
                // A concurrent close can arrive during cleanup. Use one intent snapshot;
                // the serialized close will finish any cleanup this release did not do.
                var closing = s.CloseRequested; var closeReason = s.CloseReason;
                var before = applications.List(s.Info.ControlSessionId);
                var stopped = !closing || s.Info.Mode == "observe" || applications.Terminate(s.Info.ControlSessionId);
                var after = applications.List(s.Info.ControlSessionId);
                var state = released && stopped ? closing ? closeReason == "IdleExpired" ? "expired" : "closed" : s.Info.ApiVersion == 2 || s.Info.PauseReason != null ? "blocked" : "active" : "blocked";
                s.Info = s.Info with
                {
                    State = state,
                    InputReleased = released,
                    ApplicationsStopped = stopped,
                    TerminatedProcessIds = s.Info.TerminatedProcessIds.Concat(before.Except(after).Select(a => a.ProcessId)).Distinct().ToArray(),
                    RetainedProcessIds = applications.RetainedIds(s.Info.ControlSessionId)
                };
                if(closing)
                {
                    foreach(var element in observed.Where(e=>e.Value.Session==s.Info.ControlSessionId).ToArray())observed.TryRemove(element.Key,out _);
                    foreach(var alias in observedAliases.Keys.Where(k=>k.StartsWith(s.Info.ControlSessionId+"|",StringComparison.Ordinal)).ToArray())observedAliases.TryRemove(alias,out _);
                    foreach(var window in windowOwners.Where(w=>w.Value==s.Info.ControlSessionId).ToArray()){windowOwners.TryRemove(window.Key,out _);windows.Retire(window.Key);}
                }
                // A fresh permit lets blocked control sessions perform observation only.
                s.Permit = new(); sessions.Save(s); return released && stopped;
            }
        }
        finally { s.Queue.Release(); }
    }
    private object Automation(GuiRequest r, ControlSession s, DesktopWindow w, CancellationToken cancellation, Action effect)
    {
        if (desktop is not IAutomationDesktop uia) throw new GuiException("BackendNotSupported");
        var a = r.Arguments; string T(string name) => GuiProtocol.Text(a, name);
        var selector = a.TryGetProperty("selector", out var se) ? GuiProtocol.Decode<ElementSelector>(se.GetRawText()) : null;
        ObservationNode? previous = null;
        if (!AutomationOperations.ReadOnly(r.Operation) || r.Operation == "find-item" || r.Operation=="capabilities"&&a.TryGetProperty("elementId",out _))
        {
            if (!observed.TryGetValue(T("elementId"), out var saved) || saved.Session != s.Info.ControlSessionId || saved.Window != T("windowId")) throw new GuiException("TargetScopeMismatch");
            previous = saved.Node;
            if (T("snapshotToken") != previous.SnapshotToken) throw new GuiException("StaleElement");
        }
        ObservationNode? searchRoot=null;
        if(a.TryGetProperty("searchRootElementId",out var searchId))
        {
            if(!observed.TryGetValue(searchId.GetString()!,out var savedRoot)||savedRoot.Session!=s.Info.ControlSessionId||savedRoot.Window!=T("windowId"))throw new GuiException("TargetScopeMismatch");
            searchRoot=savedRoot.Node;if(T("searchRootSnapshotToken")!=searchRoot.SnapshotToken)throw new GuiException("StaleElement");
        }
        var command = new UiaCommand(w, r.Operation, selector ?? (previous?.AutomationId.Length>0?new ElementSelector(AutomationId:previous.AutomationId):null), previous?.ElementId, previous?.SnapshotToken,
            a.TryGetProperty("value", out var value) ? value.GetString() : null,
            a.TryGetProperty("desiredState", out var ds) ? ds.GetString() : null,
            a.TryGetProperty("expanded", out var ex) ? ex.GetBoolean() : null,
            a.TryGetProperty("horizontal", out var h) ? h.GetDouble() : null,
            a.TryGetProperty("vertical", out var v) ? v.GetDouble() : null,
            a.TryGetProperty("treeView", out var view) ? view.GetString()! : "control", s.Info.ControlSessionId, a.TryGetProperty("itemName", out var itemName) ? itemName.GetString() : null){RootOnly=a.TryGetProperty("rootOnly",out var rootOnly)&&rootOnly.GetBoolean(),SearchRootRuntimeId=searchRoot?.ElementId,SearchRootSnapshotToken=searchRoot?.SnapshotToken};
        if (previous != null && !AutomationOperations.ReadOnly(r.Operation))
        {
            var preflight = uia.Automation(command with { Operation = "capabilities",IntendedOperation=r.Operation }, cancellation);
            var now = preflight.Target ?? throw new GuiException("StaleElement");
            if (s.Info.InteractionPolicy == "background-only") new ProfileRegistry(store).Check(s.Info.ApplicationProfileId, w.Process, r.Operation, now, preflight.Observation);
            if (!now.Enabled) throw new GuiException("ElementDisabled");
            var descriptor = AutomationOperations.All.Single(d => d.Operation == r.Operation);
            if (descriptor.RequiredPatterns.Any(p => !now.Patterns.Contains(p))) throw new GuiException("PatternNotSupported");
            if (a.TryGetProperty("expectedValueHash", out var expectedHash) && (!now.Value.Available || GuiProtocol.Hash(System.Text.Encoding.UTF8.GetBytes(now.Value.Text ?? "")) != expectedHash.GetString())) throw new GuiException("ValueConflict");
            lock (s.Mutation) { cancellation.ThrowIfCancellationRequested(); sessions.Check(s, true); Interaction?.Check(s); effect(); }
        }
        UiaResult result;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested(); result = uia.Automation(command, cancellation);
            if(previous!=null && !AutomationOperations.ReadOnly(r.Operation)) {Interaction?.Check(s); if(s.Info.InteractionPolicy=="background-only" && windows.Resolve(T("windowId")).Foreground) throw new GuiException("InterferenceDetected");}
            if (r.Operation != "wait-condition") break;
            var target = result.Target; var condition = T("condition");
            var expected = a.TryGetProperty("expected", out var e) ? e.ToString() : "";
            var satisfied = condition switch
            {
                "exists" => target != null,
                "absent" => (result.Observation.Complete||searchRoot!=null&&result.Observation.QueryComplete) && target == null,
                "enabled" => target?.Enabled == true,
                "value-equals" => target?.Value.Available == true && target.Value.Text == expected,
                "text-contains" => target?.Value.Available == true && target.Value.Text?.Contains(expected, StringComparison.Ordinal) == true,
                "toggle-equals" => target?.ToggleState == expected,
                _ => false
            };
            if (satisfied) break;
            Task.Delay(a.TryGetProperty("pollMilliseconds", out var poll) ? poll.GetInt32() : 200, cancellation).GetAwaiter().GetResult();
        }
        if (s.Info.ApplicationProfileId != null)
        {
            var excluded = new ProfileRegistry(store).Read(s.Info.ApplicationProfileId).Profile.RedactedAutomationIds ?? [];
            var hidden = new HashSet<string>();
            foreach (var node in result.Observation.Nodes) if (excluded.Contains(node.AutomationId) || node.ParentId != null && hidden.Contains(node.ParentId)) hidden.Add(node.ElementId);
            ObservationNode Redact(ObservationNode node) => hidden.Contains(node.ElementId) ? node with { Name = "[redacted]", Value = new(false, null, "redacted") } : node;
            result = result with { Observation = result.Observation with { Nodes = result.Observation.Nodes.Select(Redact).ToArray() }, Target = result.Target == null ? null : Redact(result.Target) };
        }
        var aliases = result.Observation.Nodes.ToDictionary(n=>n.ElementId,n=>observedAliases.GetOrAdd(s.Info.ControlSessionId+"|"+T("windowId")+"|"+n.ElementId+"|"+n.SnapshotToken,_=>"element-"+Guid.NewGuid().ToString("N")));
        ObservationNode Export(ObservationNode node)
        {
            var id = aliases[node.ElementId]; observed[id] = (s.Info.ControlSessionId, T("windowId"), node);
            return node with { ElementId = id, ParentId = node.ParentId != null && aliases.TryGetValue(node.ParentId, out var parent) ? parent : null };
        }
        var observation = result.Observation with { Root = aliases.GetValueOrDefault(result.Observation.Root, ""), Nodes = result.Observation.Nodes.Select(Export).ToArray() };
        return new { observation, target = result.Target == null ? null : Export(result.Target), operationDescriptors = r.Operation == "capabilities" ? AutomationOperations.All : null, effectState = result.EffectState,lookupKind=result.LookupKind,dispatchKind=result.DispatchKind };
    }
    private object Perform(GuiRequest r, ControlSession s, CancellationToken cancellation, Action sent, Action effect)
    {
        var a = r.Arguments; var sid = s.Info.ControlSessionId;
        string T(string name) => GuiProtocol.Text(a, name);
        bool InScope(DesktopWindow w) => s.Info.TargetScope.Contains(w.Process) || applications.List(sid).Any(p => p.Identity == w.Process) ||
            s.Info.ApplicationProfileId != null && applications.Children(sid).Contains(w.Process) && new ProfileRegistry(store).Read(s.Info.ApplicationProfileId).BinaryHashes.TryGetValue(w.Process.ImagePath, out var allowedHash) && allowedHash == w.Process.ImageHash;
        DesktopProcess[] Scope() => s.Info.TargetScope.Concat(applications.List(sid).Select(p=>p.Identity))
            .Concat(s.Info.ApplicationProfileId == null ? [] : applications.Children(sid).Where(p=>new ProfileRegistry(store).Read(s.Info.ApplicationProfileId).BinaryHashes.TryGetValue(p.ImagePath,out var hash) && hash==p.ImageHash)).Distinct().ToArray();
        DesktopWindow Window()
        {
            var id = T("windowId");
            if (!windowOwners.TryGetValue(id, out var owner) || owner != sid) throw new GuiException("TargetScopeMismatch");
            var w = windows.Resolve(id);
            if (s.Info.ApiVersion == 2 && !InScope(w)) throw new GuiException("TargetScopeMismatch");
            return w;
        }
        if (r.Operation is "find-window" or "wait-window")
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var available = windows.List(Scope());
                foreach (var foundWindow in available) windowOwners.TryAdd(foundWindow.WindowId, sid);
                string? Field(string name) => a.TryGetProperty(name, out var value) ? value.GetString() : null;
                string? ownerId = Field("ownerWindowId");
                if (ownerId != null)
                {
                    if (!windowOwners.TryGetValue(ownerId, out var ownerSession) || ownerSession != sid) throw new GuiException("TargetScopeMismatch");
                    var ownerHandle = windows.Resolve(ownerId).Handle;
                    available = available.Select(x => x with { OwnerWindowId = windows.Resolve(x.WindowId).OwnerHandle == ownerHandle ? ownerId : x.OwnerWindowId }).ToArray();
                }
                var found = WindowQuery.Find(available, Field("title"), Field("className"), ownerId, r.Operation == "wait-window");
                if (found != null) return new { window = found };
                Task.Delay(a.TryGetProperty("pollMilliseconds", out var interval) ? interval.GetInt32() : 200, cancellation).GetAwaiter().GetResult();
            }
        }
        if (r.Operation is not ("windows" or "launch" or "terminate-owned" or "release-input")) _ = Window();
        if (AutomationOperations.Contains(r.Operation)) return Automation(r, s, Window(), cancellation, effect);
        void Mutation(Action action, bool foreground = true)
        {
            lock (s.Mutation)
            {
                cancellation.ThrowIfCancellationRequested(); sessions.Check(s, true);
                if (!desktop.Valid) throw new GuiException("UnsupportedDesktop");
                Interaction?.Check(s);
                if (foreground && !Window().Foreground) throw new GuiException("ForegroundMismatch");
                action();
            }
        }
        void Down(string kind, int key) => Mutation(() => { if (kind == "button" && !desktop.PointerMatches(Window())) throw new GuiException("PointerTargetMismatch"); input.Down(sid, kind, key, effect); sent(); });
        void Up(string kind, int key) { lock (s.Mutation) { if (input.Up(sid, kind, key)) sent(); } }
        void Move(int x, int y, string captureId)
        {
            Mutation(() => { var b = Captures.Validate(T("windowId"), captureId); if (!new PixelRect(0, 0, b.Width, b.Height).Contains(x, y)) throw new GuiException("InvalidPosition"); effect(); input.Move(b.X + x, b.Y + y); sent(); });
        }
        (int X, int Y, string Capture) Position(JsonElement pos) => (pos.GetProperty("x").GetInt32(), pos.GetProperty("y").GetInt32(), GuiProtocol.Text(pos, "captureId"));
        switch (r.Operation)
        {
            case "windows":
                var list = windows.List(s.Info.ApiVersion == 2 ? Scope() : null).Where(w => s.Info.ApiVersion == 1 || InScope(windows.Resolve(w.WindowId))).ToArray();
                foreach (var w in list) windowOwners.TryAdd(w.WindowId, sid);
                // Each session gets its own registry IDs, even on the same Agent.
                list = list.Where(w => windowOwners[w.WindowId] == sid).ToArray();
                return new { windows = list };
            case "controls": return new { windowId = T("windowId"), elements = windows.Controls(T("windowId"), cancellation) };
            case "capture":
                if (s.Info.ApiVersion == 2)
                {
                    if (s.Info.ApplicationProfileId == null) throw new GuiException("CaptureNotAuthorized");
                    var profile = new ProfileRegistry(store).ValidateProcess(s.Info.ApplicationProfileId, Window().Process).Profile;
                    if (!profile.CaptureAllowed || profile.RedactedAutomationIds?.Length > 0) throw new GuiException("CaptureNotAuthorized");
                }
                return Captures.Take(T("windowId"), cancellation);
            case "focus": bool focused = false; Mutation(() => { var target = Window(); effect(); focused = desktop.Focus(target); }, false); if (!focused) throw new GuiException("FocusDenied"); return new { windowId = T("windowId"), foreground = true };
            case "close-window": Mutation(() => { var target = Window(); effect(); desktop.Close(target); }, false); return new { closeRequested = true };
            case "launch":
                OwnedApplication? owned = null;
                Mutation(() => { effect(); owned = applications.Launch(sid, T("executable"), a.GetProperty("arguments").EnumerateArray().Select(x => x.GetString()!).ToArray(), a.TryGetProperty("lifetime", out var l) ? l.GetString()! : "keep", a.TryGetProperty("workingDirectory", out var workingDirectory) ? workingDirectory.GetString() : null); }, false);
                return new { owned!.ProcessId, owned.Identity.Pid, owned.Identity.CreatedAt, owned.Identity.ImagePath, owned.Lifetime };
            case "terminate-owned":
                bool ended = false;
                Mutation(() => { effect(); ended = applications.Terminate(sid, a.GetProperty("processIds").EnumerateArray().Select(x => x.GetString()!).ToArray()); }, false);
                if (!ended) throw new GuiException("ApplicationStopFailed"); return new { applicationsStopped = true };
            case "click":
                if (a.TryGetProperty("position", out var pos)) { var p = Position(pos); Move(p.X, p.Y, p.Capture); }
                else
                {
                    var w = Window(); var element = windows.Element(T("windowId"), a.TryGetProperty("elementId", out var ei) ? ei.GetString() : null, a.TryGetProperty("automationId", out var ai) ? ai.GetString() : null, cancellation);
                    var x = element.Bounds.X + element.Bounds.Width / 2; var y = element.Bounds.Y + element.Bounds.Height / 2;
                    Mutation(() => { var now = Window(); if (!WindowRegistry.Same(w, now) || w.Bounds != now.Bounds || w.Dpi != now.Dpi) throw new GuiException("StaleElement"); if (!new PixelRect(0, 0, w.Bounds.Width, w.Bounds.Height).Contains(x, y)) throw new GuiException("ElementNotFound"); effect(); input.Move(w.Bounds.X + x, w.Bounds.Y + y); sent(); });
                }
                var button = GuiProtocol.Button(a); try { Down("button", button); } finally { Up("button", button); }
                break;
            case "type-text":
                foreach (var ch in T("text")) { try { Down("unicode", ch); } finally { Up("unicode", ch); } }
                break;
            case "key":
                var key = GuiProtocol.Key(T("key")); var action = T("action");
                if (action == "up") Up("key", key); else if (action == "down") Down("key", key); else try { Down("key", key); } finally { Up("key", key); }
                break;
            case "chord":
                var keys = a.GetProperty("keys").EnumerateArray().Select(x => GuiProtocol.Key(x.GetString()!)).ToArray();
                try { foreach (var k in keys) Down("key", k); } finally { foreach (var k in keys.Reverse()) Up("key", k); }
                break;
            case "move":
            case "wheel":
                var position = Position(a.GetProperty("position")); Move(position.X, position.Y, position.Capture);
                if (r.Operation == "wheel") Mutation(() => { _ = Captures.Validate(T("windowId"), position.Capture); if (!desktop.PointerMatches(Window())) throw new GuiException("PointerTargetMismatch"); effect(); input.Wheel(a.GetProperty("delta").GetInt32()); sent(); }); break;
            case "drag":
                var begin = a.GetProperty("start"); var end = a.GetProperty("end"); var capture = T("captureId");
                int x0 = begin.GetProperty("x").GetInt32(), y0 = begin.GetProperty("y").GetInt32(), x1 = end.GetProperty("x").GetInt32(), y1 = end.GetProperty("y").GetInt32();
                var bounds = Captures.Validate(T("windowId"), capture); if (!new PixelRect(0, 0, bounds.Width, bounds.Height).Contains(x1, y1)) throw new GuiException("InvalidPosition");
                int steps = a.TryGetProperty("steps", out var n) ? n.GetInt32() : 10; var btn = GuiProtocol.Button(a);
                Move(x0, y0, capture); try { Down("button", btn); for (int i = 1; i <= steps; i++) Move((int)Math.Round(x0 + ((double)x1 - x0) * i / steps), (int)Math.Round(y0 + ((double)y1 - y0) * i / steps), capture); } finally { Up("button", btn); }
                break;
            default: throw new GuiException("UnsupportedOperation");
        }
        return new { windowId = T("windowId") };
    }
}

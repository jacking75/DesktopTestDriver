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
    public GuiRequestHandler(StateStore store, IGuiDesktop desktop, ControlSessions sessions, GuiInput input, int maxStop = 3000)
    {
        this.store = store; this.desktop = desktop; this.sessions = sessions; this.input = input; this.maxStop = maxStop;
        journal = new(store); journal.Recover(false); windows = new(desktop); Captures = new(store, desktop, windows); applications = new(store);
    }
    public GuiResponse Result(string session, string request) => journal.Result(session, request);
    public async Task<GuiResponse> Execute(AgentCommand command)
    {
        // Re-parse at the trust boundary, including all argument validation.
        var r = GuiProtocol.Parse(GuiProtocol.Encode(command.Request));
        var s = sessions.Get(r.ControlSessionId); var changing = !GuiProtocol.ReadOnly(r.Operation);
        if (s.Info.Mode == "observe" && changing) return GuiResponse.Reject("ReadOnlySession", r);
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
                object data = await Task.Run(() => Perform(r, s, cancellation.Token, () => { sent++; effect = true; }, () => effect = true), CancellationToken.None);
                cancellation.Token.ThrowIfCancellationRequested();
                response = new("completed", r.RequestId, r.ControlSessionId, data, InputEventsSent: sent, InputReleased: input.Empty(s.Info.ControlSessionId));
            }
        }
        catch (Exception e)
        {
            bool? released = null;
            try { lock (s.Mutation) { released = s.Info.Mode == "observe" ? input.Empty(s.Info.ControlSessionId) : input.Release(s.Info.ControlSessionId); if (released != true) { s.Info = s.Info with { State = "blocked", InputReleased = released, StopReason = "InputReleaseFailed" }; sessions.Save(s); } } } catch { }
            string code = e is GuiException g ? g.Code : e is OperationCanceledException ? "RequestTimeout" : "OperationFailed";
            response = new(effect ? "failed" : "rejected", r.RequestId, r.ControlSessionId, Error: new(code, e is GuiException ? e.Message : code), InputEventsSent: sent, InputReleased: released);
        }
        finally { if (acquired) s.Queue.Release(); }
        response = response with { Operation = r.Operation, MachineId = s.Info.MachineId };
        if (r.Operation != "capture")
        {
            var data = response.Data == null ? new Dictionary<string, object?>() : GuiProtocol.Decode<Dictionary<string, object?>>(GuiProtocol.Encode(response.Data));
            data["inputEventsSent"] = response.InputEventsSent; data["inputReleased"] = response.InputReleased;
            response = response with { Data = data };
        }
        journal.Finish(r, response); return response;
    }
    public async Task<bool> Release(ControlSession s, bool close, string reason)
    {
        var started = Stopwatch.GetTimestamp();
        CancellationTokenSource old;
        // Revoke before waiting for the native dispatch gate, even when its owner is stuck.
        s.Permit.Cancel();
        if (!Monitor.TryEnter(s.Mutation, maxStop))
        {
            s.Info = s.Info with { State = "blocked", InputReleased = null, StopReason = "DispatchStopUnconfirmed" };
            sessions.Save(s); store.Write("worker-stop-needed.json", new { sessionId = s.Info.ControlSessionId, reason }); return false;
        }
        try
        {
            if (s.Info.State is "closed" or "expired") return s.Info.InputReleased == true && s.Info.ApplicationsStopped == true;
            if (close) { s.CloseRequested = true; s.CloseReason = reason; }
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
                var released = s.Info.Mode == "observe" ? input.Empty(s.Info.ControlSessionId) : input.Release(s.Info.ControlSessionId);
                var before = applications.List(s.Info.ControlSessionId);
                var stopped = !s.CloseRequested || s.Info.Mode == "observe" || applications.Terminate(s.Info.ControlSessionId);
                var after = applications.List(s.Info.ControlSessionId);
                var state = released && stopped ? s.CloseRequested ? s.CloseReason == "IdleExpired" ? "expired" : "closed" : "active" : "blocked";
                s.Info = s.Info with
                {
                    State = state,
                    InputReleased = released,
                    ApplicationsStopped = stopped,
                    TerminatedProcessIds = s.Info.TerminatedProcessIds.Concat(before.Except(after).Select(a => a.ProcessId)).Distinct().ToArray(),
                    RetainedProcessIds = applications.RetainedIds(s.Info.ControlSessionId)
                };
                // A fresh permit lets blocked control sessions perform observation only.
                s.Permit = new(); sessions.Save(s); return released && stopped;
            }
        }
        finally { s.Queue.Release(); }
    }
    private object Perform(GuiRequest r, ControlSession s, CancellationToken cancellation, Action sent, Action effect)
    {
        var a = r.Arguments; var sid = s.Info.ControlSessionId;
        string T(string name) => GuiProtocol.Text(a, name);
        DesktopWindow Window() => windows.Resolve(T("windowId"));
        void Mutation(Action action, bool foreground = true)
        {
            lock (s.Mutation)
            {
                cancellation.ThrowIfCancellationRequested(); sessions.Check(s, true);
                if (!desktop.Valid) throw new GuiException("UnsupportedDesktop");
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
            case "windows": return new { windows = windows.List() };
            case "controls": return new { windowId = T("windowId"), elements = windows.Controls(T("windowId"), cancellation) };
            case "capture": return Captures.Take(T("windowId"), cancellation);
            case "focus": bool focused = false; Mutation(() => { var target = Window(); effect(); focused = desktop.Focus(target); }, false); if (!focused) throw new GuiException("FocusDenied"); return new { windowId = T("windowId"), foreground = true };
            case "close-window": Mutation(() => { var target = Window(); effect(); desktop.Close(target); }, false); return new { closeRequested = true };
            case "launch":
                OwnedApplication? owned = null;
                Mutation(() => { effect(); owned = applications.Launch(sid, T("executable"), a.GetProperty("arguments").EnumerateArray().Select(x => x.GetString()!).ToArray(), a.TryGetProperty("lifetime", out var l) ? l.GetString()! : "keep"); }, false);
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

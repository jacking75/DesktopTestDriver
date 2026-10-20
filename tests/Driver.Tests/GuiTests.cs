using System.Diagnostics;
using System.Text.Json;
using DesktopTestDriver;
if (args is ["--watchdog", var root]) return await WatchdogRunner.Run(root);
if (args is ["--sleeper"]) { await Task.Delay(30000); return 0; }
int passed = 0;
void Check(bool condition, string message = "assertion failed") { if (!condition) throw new Exception(message); }
async Task Test(string name, Func<Task> body) { await body(); passed++; Console.WriteLine("PASS " + name); }
string Root() => Path.GetFullPath(Path.Combine("artifacts", "local-tests", Guid.NewGuid().ToString("N")));
GuiRequest Req(string session, string op, object arguments, string? id = null, int ms = 5000) => GuiProtocol.Parse(GuiProtocol.Encode(new { apiVersion = 1, requestId = id ?? "r-" + Guid.NewGuid().ToString("N"), controlSessionId = session, operation = op, timeoutMilliseconds = ms, arguments }));
void Reject(string json) { try { GuiProtocol.Parse(json); throw new Exception("invalid request accepted"); } catch (Exception e) when (e is GuiException or JsonException or InvalidOperationException or KeyNotFoundException) { } }
(StateStore Store, FakeDesktop Desktop, FakeInput Input, GuiInput Owned, ControlSessions Sessions, GuiRequestHandler Handler, SessionInfo Session) Context(int stop = 3000) { var store = new StateStore(Root()); var desktop = new FakeDesktop(); var input = new FakeInput(); var owned = new GuiInput(store, input); var sessions = new ControlSessions(store, "test", 1); var session = sessions.Open("control", 900); return (store, desktop, input, owned, sessions, new(store, desktop, sessions, owned, stop), session); }
async Task<string> Window(GuiRequestHandler handler, string sid) { var result = await handler.Execute(new(Req(sid, "windows", new { }), 5000)); Check(result.Status == "completed"); return JsonSerializer.SerializeToElement(result.Data, GuiProtocol.Json).GetProperty("windows")[0].GetProperty("windowId").GetString()!; }
await Test("G01 strict contract and canonical duplicate identity", () =>
{
    var sample = """{"apiVersion":1,"requestId":"one","controlSessionId":"session","operation":"windows","arguments":{}}""";
    Check(GuiProtocol.Fingerprint(GuiProtocol.Parse(sample)) == GuiProtocol.Fingerprint(GuiProtocol.Parse("""{"arguments":{},"operation":"windows","controlSessionId":"session","requestId":"one","timeoutMilliseconds":5000,"apiVersion":1}""")));
    Reject(sample.Replace("\"apiVersion\":1", "\"apiVersion\":1,\"apiVersion\":1")); Reject(sample.Replace("windows", "scenario")); Reject(sample.Replace("{}", "{\"extra\":1}")); Reject(sample.Replace("\"apiVersion\":1", "\"apiVersion\":2")); Reject(sample.Replace("{}", "null")); return Task.CompletedTask;
});
await Test("G01 function-key aliases cannot cause partial chord injection", () =>
{
    foreach (var alias in new[] { "F01", "F+1", "F 1" })
        Reject(GuiProtocol.Encode(new { apiVersion = 1, requestId = "alias", controlSessionId = "session", operation = "chord", arguments = new { windowId = "window", keys = new[] { "F1", alias } } }));
    return Task.CompletedTask;
});
await Test("G03 certificate pins accept uppercase hex and reject invalid configuration", () =>
{
    var folder = Root(); Directory.CreateDirectory(folder); var path = Path.Combine(folder, "Agent.json");
    var valid = new AgentSettings(Path.Combine(folder, "state"), "test", "127.0.0.1", 32550, new string('A', 64));
    File.WriteAllText(path, GuiProtocol.Encode(valid)); Check(ConnectionSettings.Agent(path).ControllerCertificate == new string('a', 64));
    foreach (var invalid in new[] { valid with { ControllerCertificate = new string('g', 64) }, valid with { ControllerCertificate = null! }, valid with { InteractiveSessionId = -1 } })
    {
        File.WriteAllText(path, GuiProtocol.Encode(invalid));
        try { ConnectionSettings.Agent(path); throw new Exception("invalid configuration accepted"); }
        catch (GuiException e) { Check(e.Code == "InvalidConfiguration"); }
    }
    return Task.CompletedTask;
});
await Test("G01 published JSON request examples match the implemented API", () =>
{
    var repository = new DirectoryInfo(AppContext.BaseDirectory);
    while (repository != null && !File.Exists(Path.Combine(repository.FullName, "DesktopTestDriver.slnx"))) repository = repository.Parent;
    Check(repository != null, "repository documentation not found"); int examples = 0;
    foreach (var file in new[] { "README.md", "docs/agent-usage.md", "docs/gui-test-guide.md" })
    {
        var markdown = File.ReadAllText(Path.Combine(repository!.FullName, file));
        foreach (System.Text.RegularExpressions.Match fence in System.Text.RegularExpressions.Regex.Matches(markdown, @"```json\s*\r?\n([\s\S]*?)```"))
        {
            var json = fence.Groups[1].Value; using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("arguments", out _)) continue;
            // The beginner manual explicitly asks callers to replace this display placeholder.
            _ = GuiProtocol.Parse(json.Replace("\"실제로-받은-세션번호\"", "\"control-example\"")); examples++;
        }
    }
    Check(examples >= 7, "published request examples were not inspected"); return Task.CompletedTask;
});
await Test("G06 one control owner and observe mutation denial", async () =>
{
    var c = Context(); try { c.Sessions.Open("control", 900); throw new Exception("second owner accepted"); } catch (GuiException e) { Check(e.Code == "ControlSessionBusy"); }
    var observe = c.Sessions.Open("observe", 900); var wid = await Window(c.Handler, observe.ControlSessionId);
    foreach (var op in new[] { "focus", "click", "type-text", "release-input", "launch", "close-window" }) { object a = op switch { "click" => new { windowId = wid, automationId = "button" }, "type-text" => new { windowId = wid, text = "text" }, "release-input" => new { }, "launch" => new { executable = Environment.ProcessPath!, arguments = new[] { "--sleeper" } }, _ => new { windowId = wid } }; var r = await c.Handler.Execute(new(Req(observe.ControlSessionId, op, a), 5000)); Check(r.Error?.Code == "ReadOnlySession", op); }
    Check(c.Input.Events.Count == 0 && c.Desktop.FocusCalls == 0);
    await c.Handler.Release(c.Sessions.Get(c.Session.ControlSessionId), true, "SessionClosed"); var closed = await c.Handler.Execute(new(Req(c.Session.ControlSessionId, "key", new { windowId = wid, key = "A", action = "press" }), 5000)); Check(closed.Error?.Code == "SessionClosed" && c.Input.Events.Count == 0);
});
await Test("G06 timed-out close cannot be reopened by release-input", async () =>
{
    var c = Context(100); var s = c.Sessions.Get(c.Session.ControlSessionId);
    using var entered = new ManualResetEventSlim(); using var unblock = new ManualResetEventSlim();
    var worker = Task.Run(() => { lock (s.Mutation) { entered.Set(); unblock.Wait(); } });
    Check(entered.Wait(1000));
    try { Check(!await c.Handler.Release(s, true, "SessionClosed")); }
    finally { unblock.Set(); await worker; }
    Check(await c.Handler.Release(s, false, "ExplicitRelease"));
    Check(s.Info.State == "closed", "release-input reopened a session whose close timed out");
});
await Test("G04 RPC partial write makes connection unusable", async () =>
{
    using var stream = new PartialWriteStream(); await using var peer = new RpcPeer(stream);
    try { await peer.Call<GuiResponse>("test", new { }, CancellationToken.None); throw new Exception("write failure accepted"); }
    catch (IOException) { }
    Check(!peer.Connected, "partially written RPC connection stayed available");
    var writes = stream.Writes;
    try { await peer.Call<GuiResponse>("again", new { }, CancellationToken.None); throw new Exception("broken stream reused"); }
    catch (GuiException e) { Check(e.Code == "AgentDisconnected"); }
    Check(stream.Writes == writes);
});
await Test("G06 close cancels queued input, blocks until worker stops", async () =>
{
    var c = Context(100); var sid = c.Session.ControlSessionId; var wid = await Window(c.Handler, sid); c.Desktop.DelayControls = 700;
    var slow = c.Handler.Execute(new(Req(sid, "controls", new { windowId = wid }), 5000)); Check(c.Desktop.Entered.Wait(1000)); var key = c.Handler.Execute(new(Req(sid, "key", new { windowId = wid, key = "A", action = "press" }), 5000));
    Check(!await c.Handler.Release(c.Sessions.Get(sid), true, "SessionClosed")); Check(c.Sessions.Get(sid).Info.State == "blocked"); await Task.WhenAll(slow, key); Check(c.Input.Events.Count == 0); Check(await c.Handler.Release(c.Sessions.Get(sid), true, "SessionClosed"));
});
await Test("G15 stuck native dispatch has bounded stop and independent recovery signal", async () =>
{
    var c = Context(100); var sid = c.Session.ControlSessionId; var wid = await Window(c.Handler, sid); c.Input.BlockDownMilliseconds = 700;
    var request = c.Handler.Execute(new(Req(sid, "key", new { windowId = wid, key = "A", action = "press" }), 5000)); Check(c.Input.DownEntered.Wait(1000)); var timer = Stopwatch.StartNew();
    Check(!await c.Handler.Release(c.Sessions.Get(sid), true, "SessionClosed") && timer.ElapsedMilliseconds < 500); Check(c.Store.Exists("worker-stop-needed.json"));
    await request; Check(await c.Handler.Release(c.Sessions.Get(sid), true, "SessionClosed") && c.Owned.Empty(sid));
});
await Test("G07 concurrent duplicate once, mismatch, retained result after restart", async () =>
{
    var c = Context(); var sid = c.Session.ControlSessionId; var wid = await Window(c.Handler, sid); var r = Req(sid, "key", new { windowId = wid, key = "A", action = "press" }); await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => c.Handler.Execute(new(r, 5000)))); Check(c.Input.Events.Count == 2);
    var original = c.Handler.Result(sid, r.RequestId); Check(original.Status == "completed"); Check(GuiProtocol.Encode(original) == GuiProtocol.Encode(await c.Handler.Execute(new(r, 5000))));
    try { await c.Handler.Execute(new(Req(sid, "key", new { windowId = wid, key = "B", action = "press" }, r.RequestId), 5000)); throw new Exception("mismatch accepted"); } catch (GuiException e) { Check(e.Code == "DuplicateRequestMismatch"); }
    var restarted = new GuiRequestHandler(c.Store, c.Desktop, c.Sessions, c.Owned); Check(restarted.Result(sid, r.RequestId).Status == "completed" && c.Input.Events.Count == 2);
});
await Test("G07 orphan admission is unknown, no replay", () =>
{
    var store = new StateStore(Root()); var j = new RequestJournal(store); var r = Req("session", "windows", new { }); j.Accept(r, Deadline.Start(5000)); new RequestJournal(store).Recover(false); Check(j.Result("session", r.RequestId).Status == "completionUnknown"); return Task.CompletedTask;
});
await Test("G07 storage failure stops injection", async () =>
{
    var c = Context(); var wid = await Window(c.Handler, c.Session.ControlSessionId); Directory.CreateDirectory(c.Store.PathFor("held-input.json")); var result = await c.Handler.Execute(new(Req(c.Session.ControlSessionId, "key", new { windowId = wid, key = "A", action = "down" }), 5000)); Check(result.Status != "completed" && c.Input.Events.Count == 0);
});
await Test("G08 title change retained, reused handle rejected", () =>
{
    var d = new FakeDesktop(); var registry = new WindowRegistry(d); var id = registry.List()[0].WindowId; d.Current = d.Current with { Title = "saved" }; Check(registry.Resolve(id).Title == "saved"); d.Current = d.Current with { Process = d.Current.Process with { CreatedAt = DateTimeOffset.UtcNow.AddHours(1) } };
    try { registry.Resolve(id); throw new Exception("reused handle accepted"); } catch (GuiException e) { Check(e.Code == "WindowChanged"); }
    Check(registry.List()[0].WindowId != id); var second = registry.List()[0].WindowId; d.Current = d.Current with { Generation = 1 }; try { registry.Resolve(second); throw new Exception("same-process handle reuse accepted"); } catch (GuiException e) { Check(e.Code == "WindowChanged"); }
    return Task.CompletedTask;
});
await Test("G09 ambiguity, stale elements, empty versus unavailable", () =>
{
    var d = new FakeDesktop(); var registry = new WindowRegistry(d); var wid = registry.List()[0].WindowId; var elements = registry.Controls(wid, default); Check(elements[0].Value.Available && elements[0].Value.Text == "" && !elements[1].Value.Available && elements[1].Value.Reason == "Password"); d.DuplicateElements = true;
    try { registry.Element(wid, null, "button", default); throw new Exception("ambiguous accepted"); } catch (GuiException e) { Check(e.Code == "AmbiguousTarget"); }
    d.DuplicateElements = false; d.ElementX = 50;
    try { registry.Element(wid, elements[0].ElementId, null, default); throw new Exception("stale accepted"); } catch (GuiException e) { Check(e.Code == "StaleElement"); }
    return Task.CompletedTask;
});
await Test("G10 focus-free capture independent of UIA, geometry validation", async () =>
{
    var c = Context(); var sid = c.Session.ControlSessionId; var wid = await Window(c.Handler, sid); c.Desktop.FailControls = true; var reply = await c.Handler.Execute(new(Req(sid, "capture", new { windowId = wid, output = Path.Combine(c.Store.Root, "out.png") }), 5000)); Check(reply.Status == "completed" && c.Desktop.FocusCalls == 0 && c.Desktop.CaptureCalls == 1); var cap = (CaptureInfo)reply.Data!; Check(c.Handler.Captures.Validate(wid, cap.CaptureId).Width == 400);
    c.Desktop.Current = c.Desktop.Current with { Foreground = false }; _ = c.Handler.Captures.Validate(wid, cap.CaptureId); c.Desktop.Current = c.Desktop.Current with { Dpi = 144 }; try { c.Handler.Captures.Validate(wid, cap.CaptureId); throw new Exception("stale capture accepted"); } catch (GuiException e) { Check(e.Code == "StaleCapture"); }
});
await Test("G10 restart restores only a verified published image", () =>
{
    var store = new StateStore(Root()); var journal = new RequestJournal(store); var output = Path.Combine(store.Root, "final.png"); var bytes = new byte[] { 1, 2, 3 }; var r = Req("session", "capture", new { windowId = "window", output }); var entry = journal.Accept(r, Deadline.Start(5000)).Entry; var info = new CaptureInfo("cap", "window", 1, 1, "window-client-pixels", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 3, GuiProtocol.Hash(bytes)); journal.Save(entry with { AgentResponse = new("completed", r.RequestId, r.ControlSessionId, info), ExpectedHash = info.Sha256, ExpectedBytes = 3 }); File.WriteAllBytes(output, bytes); new RequestJournal(store).Recover(true); Check(journal.Result("session", r.RequestId).Status == "completed");
    var missing = Req("session", "capture", new { windowId = "window", output = Path.Combine(store.Root, "missing.png") }); entry = journal.Accept(missing, Deadline.Start(5000)).Entry; journal.Save(entry with { AgentResponse = new("completed", missing.RequestId, "session", info) }); new RequestJournal(store).Recover(true); Check(journal.Result("session", missing.RequestId).Status == "failed"); return Task.CompletedTask;
});
await Test("G11 foreground guard and three button down/up pairs", async () =>
{
    var c = Context(); var sid = c.Session.ControlSessionId; var wid = await Window(c.Handler, sid); c.Desktop.Current = c.Desktop.Current with { Foreground = false }; var fail = await c.Handler.Execute(new(Req(sid, "click", new { windowId = wid, automationId = "button" }), 5000)); Check(fail.Error?.Code == "ForegroundMismatch" && c.Input.Events.Count == 0);
    Check((await c.Handler.Execute(new(Req(sid, "focus", new { windowId = wid }), 5000))).Status == "completed"); foreach (var button in new[] { "left", "right", "middle" }) Check((await c.Handler.Execute(new(Req(sid, "click", new { windowId = wid, automationId = "button", button }), 5000))).Status == "completed"); Check(c.Input.Events.Count(e => e.StartsWith("down:button")) == 3 && c.Input.Events.Count(e => e.StartsWith("up:button")) == 3);
    c.Desktop.PointerCorrect = false;
    var mismatch = await c.Handler.Execute(new(Req(sid, "click", new { windowId = wid, automationId = "button" }), 5000));
    Check(mismatch.Error?.Code == "PointerTargetMismatch" && c.Input.Events.Count(e => e.StartsWith("down:button")) == 3);

});
await Test("G12 Unicode, key/chord, partial injection recovery, external ownership", async () =>
{
    var c = Context(); var sid = c.Session.ControlSessionId; var wid = await Window(c.Handler, sid); const string text = "한글A😀"; var result = await c.Handler.Execute(new(Req(sid, "type-text", new { windowId = wid, text }), 5000)); Check(result.InputEventsSent == text.Length * 2);
    foreach (var key in new[] { "ENTER", "TAB", "F12" }) Check((await c.Handler.Execute(new(Req(sid, "key", new { windowId = wid, key, action = "press" }), 5000))).Status == "completed"); Check((await c.Handler.Execute(new(Req(sid, "chord", new { windowId = wid, keys = new[] { "CTRL", "A" } }), 5000))).Status == "completed");
    c.Input.FailNextDown = true; result = await c.Handler.Execute(new(Req(sid, "key", new { windowId = wid, key = "A", action = "press" }), 5000)); Check(result.Status == "failed" && result.InputReleased == true && c.Owned.Empty(sid)); c.Input.ExternalHeld = true; var count = c.Input.Events.Count; await c.Handler.Execute(new(Req(sid, "key", new { windowId = wid, key = "B", action = "press" }), 5000)); Check(c.Input.Events.Count == count);
});
await Test("G13 negative monitor coordinates, wheel, interrupted drag release", async () =>
{
    var c = Context(); var sid = c.Session.ControlSessionId; var wid = await Window(c.Handler, sid); var capture = await c.Handler.Execute(new(Req(sid, "capture", new { windowId = wid, output = Path.Combine(c.Store.Root, "out.png") }), 5000)); var cap = ((CaptureInfo)capture.Data!).CaptureId;
    Check((await c.Handler.Execute(new(Req(sid, "move", new { windowId = wid, position = new { x = 10, y = 20, captureId = cap } }), 5000))).Status == "completed" && c.Input.Events.Last() == "move:-190:70"); Check((await c.Handler.Execute(new(Req(sid, "wheel", new { windowId = wid, position = new { x = 10, y = 20, captureId = cap }, delta = -120 }), 5000))).Status == "completed");
    c.Input.OnMove = () => c.Desktop.Current = c.Desktop.Current with { Dpi = 144 }; var drag = await c.Handler.Execute(new(Req(sid, "drag", new { windowId = wid, captureId = cap, start = new { x = 1, y = 1 }, end = new { x = 20, y = 30 }, steps = 10 }), 5000)); Check(drag.Status == "failed" && drag.InputReleased == true && c.Owned.Empty(sid));
});
await Test("G14 real process ownership, keep/session separation", () =>
{
    var store = new StateStore(Root()); var apps = new GuiApplications(store); var keep = apps.Launch("session", Environment.ProcessPath!, ["--sleeper"], "keep"); var scoped = apps.Launch("session", Environment.ProcessPath!, ["--sleeper"], "session"); using var unrelated = Processes.Start(Environment.ProcessPath!, "--sleeper");
    try { try { apps.Terminate("other", [keep.ProcessId]); throw new Exception("unowned kill accepted"); } catch (GuiException e) { Check(e.Code == "ProcessNotOwned"); } Check(apps.Terminate("session") && Processes.Alive(keep.Identity) && !Processes.Alive(scoped.Identity) && !unrelated.HasExited); Check(apps.Terminate("session", [keep.ProcessId])); }
    finally { Processes.KillOwned(keep.Identity); Processes.KillOwned(scoped.Identity); if (!unrelated.HasExited) unrelated.Kill(); }
    return Task.CompletedTask;
});
await Test("G15 independent watchdog stops owned producer, preserves keep app", async () =>
{
    var store = new StateStore(Root()); var apps = new GuiApplications(store); var keep = apps.Launch("session", Environment.ProcessPath!, ["--sleeper"], "keep"); var scoped = apps.Launch("session", Environment.ProcessPath!, ["--sleeper"], "session"); using var producer = Processes.Start(Environment.ProcessPath!, "--sleeper");
    store.Write("agent-process.json", Processes.Identify(producer)); store.Write("agent-heartbeat.json", Stopwatch.GetTimestamp() - Stopwatch.Frequency * 4); store.Write("agent-limits.json", 1500); store.Write("session-session.json", new SessionInfo("session", "test", "user", 1, "Default", "control", "active", 900, DateTimeOffset.UtcNow.AddMinutes(15)));
    using var watchdog = Processes.Start(Environment.ProcessPath!, "--watchdog", store.Root);
    try { await watchdog.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6)); Check(watchdog.ExitCode == 0 && !Processes.Alive(store.Read<DesktopProcess>("agent-process.json")) && Processes.Alive(keep.Identity) && !Processes.Alive(scoped.Identity)); Check(store.Read<JsonElement>("watchdog-recovery.json").GetProperty("inputReleased").GetBoolean()); }
    finally { Processes.KillOwned(keep.Identity); Processes.KillOwned(scoped.Identity); if (!producer.HasExited) producer.Kill(); if (!watchdog.HasExited) watchdog.Kill(); }
});
await Test("G15 unreadable heartbeat still triggers independent recovery", async () =>
{
    var store = new StateStore(Root()); using var producer = Processes.Start(Environment.ProcessPath!, "--sleeper");
    var identity = Processes.Identify(producer); store.Write("agent-process.json", identity);
    File.WriteAllText(store.PathFor("agent-heartbeat.json"), "invalid-json"); store.Write("agent-limits.json", 1500);
    using var watchdog = Processes.Start(Environment.ProcessPath!, "--watchdog", store.Root);
    try { await watchdog.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6)); Check(watchdog.ExitCode == 0 && !Processes.Alive(identity), "watchdog exited without stopping producer"); Check(store.Read<JsonElement>("watchdog-recovery.json").GetProperty("inputReleased").GetBoolean()); }
    finally { if (!producer.HasExited) producer.Kill(); if (!watchdog.HasExited) watchdog.Kill(); }
});
await Test("G14 interrupted launch ownership prevents false cleanup success", () =>
{
    var store = new StateStore(Root());
    store.Write("launch-intent-process-unknown.json", new LaunchIntent("process-unknown", "session", "session"));
    Check(!new GuiApplications(store).Terminate("session"));
    Check(store.Exists("launch-intent-process-unknown.json"));
    return Task.CompletedTask;
});
await Test("G20 regression: atomic heartbeat replacement while independent reader runs", async () =>
{
    var store = new StateStore(Root()); store.Write("heartbeat.json", 0); using var cancellation = new CancellationTokenSource();
    var reader = Task.Run(() => { while (!cancellation.IsCancellationRequested) { var value = store.Read<int>("heartbeat.json"); Check(value >= 0); } });
    try { for (int i = 0; i < 1000; i++) store.Write("heartbeat.json", i); } finally { cancellation.Cancel(); await reader; }
});
await Test("G20 atomic state replacement survives a short Windows file lock", async () =>
{
    var store = new StateStore(Root()); store.Write("snapshot.json", 1);
    using var started = new ManualResetEventSlim();
    var blocker = new FileStream(store.PathFor("snapshot.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
    Task writer;
    try
    {
        writer = Task.Run(() => { started.Set(); store.Write("snapshot.json", 2); });
        Check(started.Wait(1000)); await Task.Delay(80);
    }
    finally { blocker.Dispose(); }
    await writer; Check(store.Read<int>("snapshot.json") == 2);
});
await Test("G20 regression: sustained Unicode cannot starve input maintenance", async () =>
{
    var store = new StateStore(Root()); var backend = new FakeInput(); var writer = new GuiInput(store, backend); var reader = new GuiInput(store, backend);
    using var finished = new CancellationTokenSource(); using var ready = new ManualResetEventSlim(); int observations = 0;
    var maintenance = Task.Run(() => { ready.Set(); while (!finished.IsCancellationRequested) { _ = reader.Overdue(1500); Interlocked.Increment(ref observations); Thread.Sleep(1); } });
    ready.Wait();
    try { await Task.Run(() => { for (int i = 0; i < 512; i++) { writer.Down("session", "unicode", 0xAC00 + i); writer.Up("session", "unicode", 0xAC00 + i); } }); }
    finally { finished.Cancel(); await maintenance; }
    Check(observations > 0 && reader.Empty("session") && backend.Events.Count == 1024);
});
passed += await IntegrationTests.Run();
Console.WriteLine(GuiProtocol.Encode(new { passed, realDesktopInput = false, twoPhysicalMachines = false })); return 0;
sealed class FakeInput : IInputBackend
{
    public readonly List<string> Events = []; public bool FailNextDown, ExternalHeld; public Action? OnMove; public Action<string, int, bool>? OnTransition; public int BlockDownMilliseconds; public readonly ManualResetEventSlim DownEntered = new();
    public bool IsDown(string kind, int code) => ExternalHeld;
    public void Down(string kind, int code) { if (FailNextDown) { FailNextDown = false; throw new GuiException("InjectionFailed"); } Events.Add($"down:{kind}:{code}"); DownEntered.Set(); if (BlockDownMilliseconds > 0) Thread.Sleep(BlockDownMilliseconds); OnTransition?.Invoke(kind, code, true); }
    public void Up(string kind, int code) { Events.Add($"up:{kind}:{code}"); OnTransition?.Invoke(kind, code, false); } public void Move(int x, int y) { Events.Add($"move:{x}:{y}"); OnMove?.Invoke(); }
    public void Wheel(int delta) => Events.Add($"wheel:{delta}");
}
sealed class PartialWriteStream : MemoryStream
{
    public int Writes;
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Writes++;
        if (Writes == 2) throw new IOException("simulated failure after RPC header");
        return base.WriteAsync(buffer, cancellationToken);
    }
}
sealed class FakeDesktop : IGuiDesktop
{
    public DesktopWindow Current = new(123, new(456, DateTimeOffset.UtcNow, "C:\\fake.exe", "hash"), "memo", new(-200, 50, 400, 300), 96, true); public bool Valid => true;
    public bool PointerCorrect = true; public string ObservedValue = "";
    public bool PointerMatches(DesktopWindow window) => PointerCorrect; public bool DuplicateElements, FailControls; public int ElementX = 10, DelayControls, FocusCalls, CaptureCalls; public readonly ManualResetEventSlim Entered = new();
    public DesktopWindow[] Windows() => [Current]; public DesktopWindow Identify(long handle) => Current;
    public DesktopElement[] Controls(DesktopWindow w, CancellationToken token) { Entered.Set(); if (DelayControls > 0) Thread.Sleep(DelayControls); token.ThrowIfCancellationRequested(); if (FailControls) throw new GuiException("ObservationUnavailable"); var e = new DesktopElement("runtime", "button", "button", "Button", new(ElementX, 10, 50, 30), true, new(true, ObservedValue, null)); return DuplicateElements ? [e, e with { RuntimeId = "other" }] : [e, e with { RuntimeId = "password", AutomationId = "password", Value = new(false, null, "Password") }]; }
    public byte[] Capture(DesktopWindow w, CancellationToken t) { CaptureCalls++; return [1, 2, 3, 4]; }
    public bool Focus(DesktopWindow w) { FocusCalls++; Current = Current with { Foreground = true }; return true; }
    public void Close(DesktopWindow w) { }
}

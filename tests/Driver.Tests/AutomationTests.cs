using DesktopTestDriver;
using System.Text.Json;

static class AutomationTests
{
    public static async Task<int> Run()
    {
        int passed = 0;
        void Check(bool ok) { if (!ok) throw new Exception("automation assertion failed"); }
        void Reject(Action action, string code) { try { action(); throw new Exception("expected rejection " + code); } catch (GuiException e) { Check(e.Code == code); } }
        GuiRequest Request(string sid, string op, object arguments, string? id = null, int version = 2) => GuiProtocol.Parse(GuiProtocol.Encode(new { apiVersion = version, requestId = id ?? Guid.NewGuid().ToString("N"), controlSessionId = sid, operation = op, arguments, timeoutMilliseconds = 5000 }));
        ObservationNode Node(string id, string? parent, string name, bool enabled = true) => new(id, parent, "", name, "Button", "Button", "fixture", enabled, false, new(false, null, "unavailable"), ["Invoke"], "snapshot");
        var observation = new Observation("root", [Node("root", null, "root"), Node("a", "root", "중복"), Node("b", "root", "중복"), Node("c", "a", "비활성", false)], DateTimeOffset.UtcNow, "control", true, null, [], "unknown");
        Reject(() => ElementQuery.Find(observation, new(Name: "중복")), "AmbiguousTarget");
        Check(ElementQuery.Find(observation, new(Name: "비활성", Parent: new(Name: "중복")))?.Enabled == false);
        Reject(() => ElementQuery.Find(observation with { Complete = false, TruncationReason = "node-limit" }, new(Name: "없음"), true), "SearchIncomplete");
        Reject(() => ElementQuery.Find(observation with { ProviderErrors = ["provider"] }, new(Name: "없음"), true), "SearchIncomplete");
        Check(ElementQuery.Find(observation, new(Name: "없음"), true) == null);
        passed++; Console.WriteLine("PASS S06 parent selector, duplicate, disabled and incomplete absence");
        var wide = observation with { Nodes = new[] { Node("root", null, "root") with { AutomationId = "root-id" } }
            .Concat(Enumerable.Range(0, 4000).Select(i => Node("child-" + i, "root", "child"))).ToArray() };
        var parentSelector = new ElementSelector(Parent: new(Name: "missing"));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var reference = wide.Nodes.Where(n => ElementQuery.Matches(n, parentSelector, wide)).ToArray();
        var referenceMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        Check(ElementQuery.Find(wide, parentSelector, true) == null && reference.Length == 0);
        var indexedMs = timer.Elapsed.TotalMilliseconds;
        Reject(() => ElementQuery.Find(wide, new(AncestorAutomationId: "root-id")), "AmbiguousTarget");
        Check(ElementQuery.Find(wide, new(Name: "root"))?.ElementId == "root");
        passed++; Console.WriteLine($"PASS 부모 탐색 4001개 노드: 순차 참조={referenceMs:F2}ms, 인덱스={indexedMs:F2}ms; 중복 판정 보존");
        var valid = Request("session", "set-value", new { windowId = "window", elementId = "element", snapshotToken = "snapshot", value = "한글\n" }); Check(valid.ApiVersion == 2);
        Reject(() => Request("session", "set-toggle", new { windowId = "window", elementId = "element", snapshotToken = "snapshot", desiredState = "maybe" }), "InvalidToggleState");
        Reject(() => Request("session", "find-element", new { windowId = "window", selector = new { } }), "InvalidSelector");
        Reject(() => Request("session", "wait-condition", new { windowId = "window", selector = new { name = "a" }, condition = "absent", pollMilliseconds = 1 }), "InvalidPollInterval");
        Reject(() => Request("session", "invoke", new { windowId = "window", elementId = "element", snapshotToken = "snapshot" }, version: 1), "UnsupportedOperation");
        passed++; Console.WriteLine("PASS S02 API 1/2 strict operation and selector validation");
        var store = new StateStore(Path.GetFullPath(Path.Combine("artifacts", "automation-tests", Guid.NewGuid().ToString("N"))));
        var sessions = new ControlSessions(store, "test", 1);
        var desktop = new FakeDesktop(); var backend = new FakeInput(); var input = new GuiInput(store, backend);
        var handler = new GuiRequestHandler(store, desktop, sessions, input);
        var session = sessions.Open("control", 900, 2);
        var blocked = await handler.Execute(new(Request(session.ControlSessionId, "focus", new { windowId = "anything" }, version: 1), 5000));
        Check(blocked.Error?.Code == "BackgroundQualificationRequired" && backend.Events.Count == 0 && desktop.FocusCalls == 0);
        Reject(() => sessions.Open("observe", 900, 2, "isolated-desktop"), "DedicatedMachineRequired");
        passed++; Console.WriteLine("PASS S07 shared Agent rejects API 1 mutation bypass and dedicated policy");
        var first = sessions.Open("observe", 900, 2); var second = sessions.Open("observe", 900, 2);
        var windows = await handler.Execute(new(Request(first.ControlSessionId, "windows", new { }), 5000));
        Check(JsonSerializer.SerializeToElement(windows.Data, GuiProtocol.Json).GetProperty("windows").GetArrayLength() == 0);
        var legacy = sessions.Open("observe", 900);
        var listed = await handler.Execute(new(Request(legacy.ControlSessionId, "windows", new { }, version: 1), 5000));
        var windowId = JsonSerializer.SerializeToElement(listed.Data, GuiProtocol.Json).GetProperty("windows")[0].GetProperty("windowId").GetString()!;
        var cross = await handler.Execute(new(Request(second.ControlSessionId, "controls", new { windowId }, version: 1), 5000));
        Check(cross.Error?.Code == "TargetScopeMismatch");
        passed++; Console.WriteLine("PASS S05 empty API 2 scope and cross-session API 1 read bypass rejected");
        var paused = sessions.Pause(session.ControlSessionId, "test");
        Reject(() => sessions.Resume(session.ControlSessionId, paused.Revision - 1), "RevisionMismatch");
        Check(sessions.Resume(session.ControlSessionId, paused.Revision).State == "active");
        var release = await handler.Execute(new(Request(session.ControlSessionId, "release-input", new { }), 5000));
        Check(release.Status == "completed" && sessions.Get(session.ControlSessionId).Info.State == "blocked");
        passed++; Console.WriteLine("PASS S07 explicit revision resume and API 2 input release remains paused");
        return passed;
    }
}

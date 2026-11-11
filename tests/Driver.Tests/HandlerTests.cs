using System.Diagnostics;
using System.Text.Json;
using DesktopTestDriver;
using static T;

/// <summary>Request handling against the fake desktop: result classification, session state, journaling.</summary>
internal static class HandlerTests
{
    internal sealed class Context
    {
        public readonly StateStore Store = new(Root());
        public readonly FakeDesktop Desktop = new();
        public readonly FakeInput Input = new();
        public readonly ControlSessions Sessions;
        public readonly GuiRequestHandler Handler;
        public readonly string Session;
        public string Window = "";
        public Context(bool input = false, string mode = "control")
        {
            Sessions = new(Store, "test");
            Handler = new(Store, Desktop, Sessions, new GuiInput(Store, Input), 3000) { MachineId = "test" };
            Session = Sessions.Open(mode, input, 900, new TargetSpec { Any = true }).ControlSessionId;
        }
        public Task<GuiResponse> Run(string op, object args, string? id = null, int timeout = 5000) =>
            Handler.Execute(new(new GuiRequest(id ?? "r-" + Guid.NewGuid().ToString("N")[..12], Session, op, timeout, JsonSerializer.SerializeToElement(args, GuiProtocol.Json)), timeout));
        public async Task<string> OpenWindow() => Window = Data(Ok(await Run("windows", new { }))).GetProperty("windows")[0].GetProperty("windowId").GetString()!;
        public async Task<(string Id, string Token)> Find(object selector)
        {
            var target = Data(Ok(await Run("find-element", new { windowId = Window, selector }))).GetProperty("target");
            return (target.GetProperty("elementId").GetString()!, target.GetProperty("snapshotToken").GetString()!);
        }
        public string State => Sessions.Get(Session).Info.State;
        public int JournalFiles => Directory.GetFiles(Store.Root, "request-*.json").Length;
    }
    public static IEnumerable<(string, Func<Task>)> All =>
    [
        ("H01 2.1 reads write nothing to the journal; mutations are journaled and deduplicated", async () =>
        {
            var c = new Context(); await c.OpenWindow();
            var (id, token) = await c.Find(new { automationId = "MemoText" });
            Ok(await c.Run("inspect", new { windowId = c.Window }));
            Equal(0, c.JournalFiles, "journal files after reads");
            var first = Ok(await c.Run("set-value", new { windowId = c.Window, elementId = id, snapshotToken = token, value = "가" }, "same-id"));
            var second = await c.Run("set-value", new { windowId = c.Window, elementId = id, snapshotToken = token, value = "가" }, "same-id");
            Equal(first.ObservedAt, second.ObservedAt, "duplicate returns the recorded response"); Equal(1, c.JournalFiles, "journal files");
            Equal("가", c.Desktop.Find("MemoText").Value, "applied once");
            var mismatch = await c.Run("set-value", new { windowId = c.Window, elementId = id, snapshotToken = token, value = "나" }, "same-id");
            Equal("DuplicateRequestMismatch", mismatch.Error?.Code, "mismatch");
        }),
        ("H02 2.1 5,000 reads do not slow down the next mutation", async () =>
        {
            var c = new Context(); await c.OpenWindow(); var (id, token) = await c.Find(new { automationId = "SaveButton" });
            for (int i = 0; i < 5000; i++) await c.Run("capabilities", new { windowId = c.Window, elementId = id, snapshotToken = token });
            var timer = Stopwatch.StartNew(); var r = Ok(await c.Run("invoke", new { windowId = c.Window, elementId = id, snapshotToken = token }));
            Check(timer.ElapsedMilliseconds < 500, "mutation took " + timer.ElapsedMilliseconds + " ms"); Equal(1, c.JournalFiles, "journal files");
        }),
        ("H03 3.2 refusal before dispatch is rejected and the session stays active", async () =>
        {
            var c = new Context(); await c.OpenWindow();
            var (id, token) = await c.Find(new { automationId = "Result" });
            var r = await c.Run("set-value", new { windowId = c.Window, elementId = id, snapshotToken = token, value = "x" });
            Equal("rejected", r.Status, "status"); Equal("ValueReadOnly", r.Error?.Code, "code"); Equal("active", c.State, "session");
            var (disabled, dt) = await c.Find(new { automationId = "Disabled" });
            r = await c.Run("invoke", new { windowId = c.Window, elementId = disabled, snapshotToken = dt });
            Equal("rejected", r.Status, "status"); Equal("ElementDisabled", r.Error?.Code, "code"); Equal("active", c.State, "session");
            r = await c.Run("set-toggle", new { windowId = c.Window, elementId = id, snapshotToken = token, desiredState = "on" });
            Equal("PatternNotSupported", r.Error?.Code, "code"); Equal("rejected", r.Status, "status"); Equal("active", c.State, "session");
        }),
        ("H04 completionUnknown only when a dispatch began and its end is unconfirmed; it blocks the session", async () =>
        {
            var c = new Context(); await c.OpenWindow(); var (id, token) = await c.Find(new { automationId = "SaveButton" });
            c.Desktop.FailAfterDispatch = true;
            var r = await c.Run("invoke", new { windowId = c.Window, elementId = id, snapshotToken = token });
            Equal("completionUnknown", r.Status, "status"); Equal("blocked", c.State, "session");
            Equal("SessionBlocked", (await c.Run("invoke", new { windowId = c.Window, elementId = id, snapshotToken = token })).Error?.Code, "mutation refused");
            Ok(await c.Run("find-element", new { windowId = c.Window, selector = new { automationId = "SaveButton" } }));
            c.Sessions.Reconcile(c.Session, r.RequestId!, "no-effect-confirmed", null, null);
            c.Sessions.Resume(c.Session, null); Equal("active", c.State, "resumed");
        }),
        ("H05 3.1/8.3 wait timeout is completed with satisfied=false and the session is untouched", async () =>
        {
            var c = new Context(); await c.OpenWindow();
            var timer = Stopwatch.StartNew();
            var r = Ok(await c.Run("wait-condition", new { windowId = c.Window, selector = new { automationId = "Nope" }, condition = "exists", pollMilliseconds = 25 }, timeout: 300));
            Check(timer.ElapsedMilliseconds < 1000, "took " + timer.ElapsedMilliseconds);
            Equal(false, Data(r).GetProperty("satisfied").GetBoolean(), "satisfied"); Check(Data(r).GetProperty("polls").GetInt32() > 1, "polled");
            Equal("active", c.State, "session");
        }),
        ("H06 8.3 conditions on Name, anyOf and absent", async () =>
        {
            var c = new Context(); await c.OpenWindow();
            var r = Ok(await c.Run("wait-condition", new { windowId = c.Window, selector = new { automationId = "SaveButton" }, condition = "name-contains", expected = "저" }));
            Check(Data(r).GetProperty("satisfied").GetBoolean(), "name-contains");
            r = Ok(await c.Run("wait-condition", new { windowId = c.Window, anyOf = new object[] { new { selector = new { automationId = "Error" }, condition = "exists" }, new { selector = new { automationId = "Result" }, condition = "value-equals", expected = "처음" } } }));
            Equal(1, Data(r).GetProperty("matchedIndex").GetInt32(), "matchedIndex");
            r = Ok(await c.Run("wait-condition", new { windowId = c.Window, selector = new { automationId = "Gone" }, condition = "absent" }));
            Check(Data(r).GetProperty("satisfied").GetBoolean(), "absent");
            r = await c.Run("wait-condition", new { windowId = c.Window, selector = new { name = "중복" }, condition = "exists" });
            Equal("AmbiguousTarget", r.Error?.Code, "ambiguity fails at once");
        }),
        ("H07 8.2 ambiguity lists candidates as references; index picks one", async () =>
        {
            var c = new Context(); await c.OpenWindow();
            var r = await c.Run("find-element", new { windowId = c.Window, selector = new { name = "중복" } });
            Equal("AmbiguousTarget", r.Error?.Code, "code");
            var data = GuiProtocol.Element(r.Error!.Data);
            Equal(2, data.GetProperty("matchCount").GetInt32(), "matchCount");
            Check(data.GetProperty("candidates")[0].GetProperty("elementId").GetString()!.StartsWith('e'), "candidate ref");
            var second = Ok(await c.Run("find-element", new { windowId = c.Window, selector = new { name = "중복", index = 1 } }));
            Equal(2, Data(second).GetProperty("matchCount").GetInt32(), "matchCount");
        }),
        ("H08 9.6 expect returns the observed result with the action", async () =>
        {
            var c = new Context(); await c.OpenWindow(); var (id, token) = await c.Find(new { automationId = "MemoText" });
            var r = Ok(await c.Run("set-value", new { windowId = c.Window, elementId = id, snapshotToken = token, value = "완료", expect = new { selector = new { automationId = "MemoText" }, condition = "value-equals", expected = "완료" }, observeAfter = new { maxDepth = 2 } }));
            Check(Data(r).GetProperty("expect").GetProperty("satisfied").GetBoolean(), "expect satisfied");
            Check(Data(r).GetProperty("after").GetProperty("tree").GetString()!.Contains("#MemoText"), "after tree");
        }),
        ("H09 7.1 inspect is compact by default and json on request", async () =>
        {
            var c = new Context(); await c.OpenWindow();
            var tree = Data(Ok(await c.Run("inspect", new { windowId = c.Window }))).GetProperty("tree").GetString()!;
            Check(tree.StartsWith("- Window \"메모\" [e") && tree.Contains("\n  - Edit #MemoText"), tree);
            var json = Data(Ok(await c.Run("inspect", new { windowId = c.Window, format = "json" }))).GetProperty("tree");
            Equal(JsonValueKind.Array, json.ValueKind, "json nodes");
        }),
        ("H10 observe sessions refuse mutations; real input needs an input session", async () =>
        {
            var c = new Context(mode: "observe"); await c.OpenWindow(); var (id, token) = await c.Find(new { automationId = "SaveButton" });
            Equal("ReadOnlySession", (await c.Run("invoke", new { windowId = c.Window, elementId = id, snapshotToken = token })).Error?.Code, "observe");
            var d = new Context(); await d.OpenWindow();
            Equal("InputSessionRequired", (await d.Run("type-text", new { windowId = d.Window, text = "a" })).Error?.Code, "input");
        }),
        ("H11 5.2 typing sends one batch per 500 events and writes no ledger", async () =>
        {
            var c = new Context(input: true); await c.OpenWindow();
            var ledgerBefore = File.Exists(c.Store.PathFor("held-input.json")) ? File.GetLastWriteTimeUtc(c.Store.PathFor("held-input.json")) : DateTime.MinValue;
            var r = Ok(await c.Run("type-text", new { windowId = c.Window, text = new string('가', 600) }));
            Equal(1200, Data(r).GetProperty("inputEventsSent").GetInt32(), "events");
            Equal(3, c.Input.SendCalls, "SendInput calls");
            var ledgerAfter = File.Exists(c.Store.PathFor("held-input.json")) ? File.GetLastWriteTimeUtc(c.Store.PathFor("held-input.json")) : DateTime.MinValue;
            Equal(ledgerBefore, ledgerAfter, "ledger untouched");
        }),
        ("H12 3.4 release-input leaves the session active when nothing is pending", async () =>
        {
            var c = new Context(input: true); await c.OpenWindow();
            Ok(await c.Run("key", new { windowId = c.Window, key = "SHIFT", action = "down" }));
            Check(c.Input.Down.Contains(("key", 0x10)), "held");
            var r = Ok(await c.Run("release-input", new { }));
            Check(!c.Input.Down.Contains(("key", 0x10)), "released"); Equal("active", c.State, "session");
        }),
        ("H13 3.3 a failed read does not release held input", async () =>
        {
            var c = new Context(input: true); await c.OpenWindow();
            Ok(await c.Run("key", new { windowId = c.Window, key = "SHIFT", action = "down" }));
            Equal("ElementNotFound", (await c.Run("find-element", new { windowId = c.Window, selector = new { automationId = "Nope" } })).Error?.Code, "read failed");
            Check(c.Input.Down.Contains(("key", 0x10)), "still held");
            Ok(await c.Run("release-input", new { }));
        }),
        ("H14 9.7 not-found errors carry window context; nearest elements come from the worker", async () =>
        {
            var c = new Context(); await c.OpenWindow();
            var r = await c.Run("find-element", new { windowId = c.Window, selector = new { automationId = "Nope" } });
            var context = GuiProtocol.Element(r.Error!.Data).GetProperty("context");
            Check(context.GetProperty("tree").GetString()!.Contains("#SaveButton"), "context tree");
            r = await c.Run("find-element", new { windowId = c.Window, selector = new { automationId = "Nope" }, errorContext = false });
            Check(r.Error!.Data == null, "context disabled");
        }),
        ("H16 close-window reports whether the window actually closed; wait-window absent waits for it to go", async () =>
        {
            var c = new Context(); await c.OpenWindow();
            var missing = Ok(await c.Run("wait-window", new { title = "다른 창", absent = true }));
            Check(Data(missing).GetProperty("satisfied").GetBoolean(), "absent window satisfied at once");
            var present = Ok(await c.Run("wait-window", new { title = "간단 메모", absent = true }, timeout: 300));
            Check(!Data(present).GetProperty("satisfied").GetBoolean() && Data(present).GetProperty("windows").GetArrayLength() == 1, "present window keeps absent unsatisfied");
            var closed = Ok(await c.Run("close-window", new { windowId = c.Window }));
            Check(Data(closed).GetProperty("closed").GetBoolean(), "closed");
            Check(c.Sessions.Get(c.Session).Info.ClosedWindowIds.Contains(c.Window), "closed window recorded in the session");
            Equal((bool?)null, c.Sessions.Get(c.Session).Info.ApplicationsStopped, "no cleanup result while open");
            Equal("InvalidTimeout", (await c.Run("close-window", new { windowId = c.Window, waitMilliseconds = 6000 })).Error?.Code, "wait range");
        }),
        ("H17 an action that closes its own window reports windowClosed instead of an error", async () =>
        {
            var c = new Context(); await c.OpenWindow(); var (id, token) = await c.Find(new { automationId = "SaveButton" });
            c.Desktop.CloseOnInvoke = true;
            var r = Data(Ok(await c.Run("invoke", new { windowId = c.Window, elementId = id, snapshotToken = token, observeAfter = new { includeNewWindows = true }, expect = new { selector = new { automationId = "MemoText" }, condition = "exists" } })));
            Check(r.GetProperty("windowClosed").GetBoolean() && r.GetProperty("after").GetProperty("windowClosed").GetBoolean(), "windowClosed");
            Check(!r.TryGetProperty("afterError", out _), "no afterError");
            Equal("WindowClosed", r.GetProperty("expect").GetProperty("skipped").GetString(), "expect skipped");
        }),
        ("H15 capture is journaled; stale references are refused with StaleElement", async () =>
        {
            var c = new Context(); await c.OpenWindow();
            var capture = Ok(await c.Run("capture", new { windowId = c.Window, scale = 0.5 }));
            Equal(1, c.JournalFiles, "capture journaled");
            var (id, token) = await c.Find(new { automationId = "SaveButton" });
            c.Desktop.Find("SaveButton").Name = "다른 이름";
            Equal("StaleElement", (await c.Run("invoke", new { windowId = c.Window, elementId = id, snapshotToken = token })).Error?.Code, "stale");
            Equal("active", c.State, "session");
        }),
    ];
}

using System.Diagnostics;
using System.Text.Json;
using DesktopTestDriver;
using static T;

/// <summary>Real desktop tests: UIA only, no keyboard or mouse input. Fixture windows start "quiet" (no activation).
/// Windows' own GUIs (Settings, Control Panel) are checked narrowly: open, find elements, read, close.</summary>
internal static class UiTests
{
    static string Fixture(string name) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", name, name + ".exe"));
    internal sealed class Real : IAsyncDisposable
    {
        public readonly ControllerSettings Controller;
        private readonly CancellationTokenSource stop = new();
        private readonly Task controller, agent;
        private readonly WindowsDesktop desktop;
        public Real()
        {
            var root = Root("ui-tests"); var port = 25150 + Random.Shared.Next(50);
            Controller = new ControllerSettings(Path.Combine(root, "controller"), "127.0.0.1", port);
            var host = new ControllerHost(Controller); controller = Task.Run(() => host.Run(stop.Token));
            desktop = new WindowsDesktop(Process.GetCurrentProcess().SessionId, Environment.ProcessPath!, Path.Combine(root, "capture-work"));
            var a = new AgentHost(new AgentSettings(Path.Combine(root, "agent"), "ui-pc", "127.0.0.1", port), desktop, new FakeInput());
            agent = Task.Run(() => a.Run(Environment.ProcessPath!, stop.Token, watchdog: false));
        }
        public async Task Ready() { for (int i = 0; i < 100; i++) { if ((await Call(new("status", MachineId: "ui-pc"))).Status == "completed") return; await Task.Delay(50); } throw new Exception("agent did not connect"); }
        public Task<GuiResponse> Call(LocalCommand c) => ControllerHost.Call(Controller, c);
        public string Session = "";
        public async Task<string> Launch(string fixture, params string[] args)
        {
            var opened = Ok(await Call(new("session-open", MachineId: "ui-pc") { Launch = Fixture(fixture), LaunchArguments = args }));
            Session = opened.ControlSessionId!;
            return Data(opened).GetProperty("launch").GetProperty("window").GetProperty("windowId").GetString()!;
        }
        public Task<GuiResponse> Run(string op, object args, int timeout = 5000, string? id = null) =>
            Call(new("request", Request: GuiProtocol.Encode(new GuiRequest(id ?? "r-" + Guid.NewGuid().ToString("N")[..12], Session, op, timeout, JsonSerializer.SerializeToElement(args, GuiProtocol.Json)))));
        public async Task<(string Id, string Token)> Find(string window, object selector, string? completeness = null)
        {
            var t = Data(Ok(await Run("find-element", new { windowId = window, selector, completeness }))).GetProperty("target");
            return (t.GetProperty("elementId").GetString()!, t.GetProperty("snapshotToken").GetString()!);
        }
        public async Task<JsonElement> Act(string op, string window, (string Id, string Token) e, object? extra = null)
        {
            var args = JsonSerializer.SerializeToNode(new { windowId = window, elementId = e.Id, snapshotToken = e.Token })!.AsObject();
            if (extra != null) foreach (var p in JsonSerializer.SerializeToNode(extra, GuiProtocol.Json)!.AsObject()) args[p.Key] = p.Value?.DeepClone();
            return Data(Ok(await Run(op, args)));
        }
        public async Task<bool> Wait(string window, object selector, string condition, string? expected = null, int timeout = 5000) =>
            Data(Ok(await Run("wait-condition", new { windowId = window, selector, condition, expected }, timeout))).GetProperty("satisfied").GetBoolean();
        public async ValueTask DisposeAsync()
        {
            if (Session.Length > 0) await Call(new("session-close", SessionId: Session));
            stop.Cancel(); try { await Task.WhenAll(controller, agent).WaitAsync(TimeSpan.FromSeconds(15)); } catch { }
            desktop.Dispose();
        }
    }
    public static IEnumerable<(string Name, Func<Task> Body)> All =>
    [
        ("U01 SimpleMemo: launch with the session, compact tree, set-value with expect, selector generation, cleanup", async () =>
        {
            await using var r = new Real(); await r.Ready();
            var window = await r.Launch("SimpleMemo", "--quiet");
            var tree = Data(Ok(await r.Run("inspect", new { windowId = window }))).GetProperty("tree").GetString()!;
            Check(tree.Contains("#MemoText") && tree.Contains("#SaveButton") && tree.Contains(" t="), tree);
            Check(tree.IndexOf("#TitleBar") < tree.IndexOf("#Minimize") && tree.IndexOf("#Minimize") < tree.IndexOf("#OpenButton"), "children follow their parent: " + tree);
            var memo = await r.Find(window, new { automationId = "MemoText" });
            var result = await r.Act("set-value", window, memo, new { value = "테스트 메모", expect = new { selector = new { automationId = "MemoText" }, condition = "value-equals", expected = "테스트 메모" } });
            Check(result.GetProperty("expect").GetProperty("satisfied").GetBoolean(), "expect");
            var selector = Data(Ok(await r.Run("generate-selector", new { windowId = window, elementId = memo.Id, snapshotToken = memo.Token })));
            Equal("MemoText", selector.GetProperty("selector").GetProperty("automationId").GetString(), "generated selector");
            var text = Data(Ok(await r.Run("read-text", new { windowId = window, elementId = memo.Id, snapshotToken = memo.Token })));
            Equal("테스트 메모", text.GetProperty("text").GetString(), "read-text");
            var pid = Data(Ok(await r.Run("windows", new { }))).GetProperty("windows")[0].GetProperty("pid").GetInt32();
            var closed = Ok(await r.Call(new("session-close", SessionId: r.Session))); r.Session = "";
            Check(Data(closed).GetProperty("terminatedProcessIds").GetArrayLength() == 1, "application stopped");
            Check(!Process.GetProcesses().Any(p => p.Id == pid && p.ProcessName == "SimpleMemo"), "process gone");
        }),
        ("U02 FormsWorkbench: actions, ambiguity, refusals before dispatch, password, capture", async () =>
        {
            await using var r = new Real(); await r.Ready();
            var window = await r.Launch("FormsWorkbench", "--quiet");
            await r.Act("set-value", window, await r.Find(window, new { automationId = "Editor" }), new { value = "입력값" });
            await r.Act("invoke", window, await r.Find(window, new { automationId = "Apply" }));
            Check(await r.Wait(window, new { automationId = "Result" }, "value-equals", "입력값"), "result updated");
            var option = await r.Find(window, new { automationId = "Option" });
            await r.Act("set-toggle", window, option, new { desiredState = "on" });
            Check(await r.Wait(window, new { automationId = "Option" }, "toggle-equals", "on"), "toggle on");
            var ambiguous = await r.Run("find-element", new { windowId = window, selector = new { name = "중복", controlType = "Button" } });
            Equal("AmbiguousTarget", ambiguous.Error?.Code, "ambiguous"); Equal(2, GuiProtocol.Element(ambiguous.Error!.Data).GetProperty("matchCount").GetInt32(), "matchCount");
            await r.Find(window, new { name = "중복", controlType = "Button", index = 1 });
            var disabled = await r.Find(window, new { automationId = "Disabled" });
            var refused = await r.Run("invoke", new { windowId = window, elementId = disabled.Id, snapshotToken = disabled.Token });
            Equal("rejected", refused.Status, "disabled status"); Equal("ElementDisabled", refused.Error?.Code, "disabled code");
            var readOnly = await r.Find(window, new { automationId = "Result" });
            refused = await r.Run("set-value", new { windowId = window, elementId = readOnly.Id, snapshotToken = readOnly.Token, value = "x" });
            Equal("rejected", refused.Status, "read-only status"); Equal("ValueReadOnly", refused.Error?.Code, "read-only code");
            Equal("active", Data(Ok(await r.Call(new("session-status", SessionId: r.Session)))).GetProperty("state").GetString(), "session active");
            var password = Data(Ok(await r.Run("find-element", new { windowId = window, selector = new { automationId = "Password" } }))).GetProperty("target");
            Equal("password", password.GetProperty("value").GetProperty("reason").GetString(), "password value hidden by provider");
            var capture = Data(Ok(await r.Run("capture", new { windowId = window, scale = 0.5 })));
            Check(File.Exists(capture.GetProperty("path").GetString()) && capture.GetProperty("imageWidth").GetInt32() > 100, "capture");
            var apply = await r.Find(window, new { automationId = "Apply" });
            var element = Data(Ok(await r.Run("capture", new { windowId = window, elementId = apply.Id, snapshotToken = apply.Token })));
            Check(element.GetProperty("imageWidth").GetInt32() < 200, "element capture is small");
        }),
        ("U03 WpfWorkbench: virtualization rule, three-state toggle, range, delayed wait, menu popup, virtual items", async () =>
        {
            await using var r = new Real(); await r.Ready();
            var window = await r.Launch("WpfWorkbench", "--quiet");
            var apply = Data(Ok(await r.Run("find-element", new { windowId = window, selector = new { automationId = "Apply" } })));
            Equal("outside-virtualized", apply.GetProperty("uniquenessScope").GetString(), "8.1 automationId outside containers");
            var byName = await r.Run("find-element", new { windowId = window, selector = new { name = "적용", controlType = "Button" } });
            Equal("SearchIncomplete", byName.Error?.Code, "8.1 name-only needs realized");
            var realized = Data(Ok(await r.Run("find-element", new { windowId = window, selector = new { name = "적용", controlType = "Button" }, completeness = "realized" })));
            Equal("realized-only", realized.GetProperty("uniquenessScope").GetString(), "realized");
            var tri = await r.Find(window, new { automationId = "TriState" });
            await r.Act("set-toggle", window, tri, new { desiredState = "indeterminate" });
            Check(await r.Wait(window, new { automationId = "TriState" }, "toggle-equals", "indeterminate"), "indeterminate");
            await r.Act("set-toggle", window, await r.Find(window, new { automationId = "TriState" }), new { desiredState = "off" });
            Check(await r.Wait(window, new { automationId = "TriState" }, "toggle-equals", "off"), "9.1 off from indeterminate");
            await r.Act("set-range-value", window, await r.Find(window, new { automationId = "Volume" }), new { value = 55 });
            var volume = Data(Ok(await r.Run("find-element", new { windowId = window, selector = new { automationId = "Volume" } }))).GetProperty("target");
            Equal(55.0, volume.GetProperty("rangeValue").GetProperty("value").GetDouble(), "range value");
            await r.Act("invoke", window, await r.Find(window, new { automationId = "Slow" }));
            Check(await r.Wait(window, new { automationId = "Status" }, "name-equals", "지연 완료", 5000), "8.3 name-equals after delay");
            await r.Act("set-expanded", window, await r.Find(window, new { automationId = "FileMenu" }), new { expanded = true });
            await Task.Delay(300);
            var popups = Data(Ok(await r.Run("windows", new { kind = "popup" }))).GetProperty("windows");
            Check(popups.GetArrayLength() >= 1, "6.3 menu popup listed");
            var popup = popups[0].GetProperty("windowId").GetString()!;
            await r.Act("invoke", popup, await r.Find(popup, new { automationId = "NewItem" }));
            Check(await r.Wait(window, new { automationId = "Status" }, "name-equals", "새로 만들기 선택"), "menu item invoked");
            var list = await r.Find(window, new { automationId = "Items" });
            var item = Data(Ok(await r.Run("find-item", new { windowId = window, elementId = list.Id, snapshotToken = list.Token, itemName = "항목 300" }))).GetProperty("target");
            await r.Act("scroll-item-into-view", window, (item.GetProperty("elementId").GetString()!, item.GetProperty("snapshotToken").GetString()!));
            await r.Find(window, new { name = "항목 300", controlType = "ListItem" }, "realized");
            var full = Data(Ok(await r.Run("inspect", new { windowId = window }))).GetProperty("nodeCount").GetInt32();
            var interactive = Data(Ok(await r.Run("inspect", new { windowId = window, interactiveOnly = true, maxDepth = 6 }))).GetProperty("nodeCount").GetInt32();
            Check(interactive < full, $"interactiveOnly {interactive} < {full}");
            // close-window with unsaved input: the window stays, the prompt is reported, answering it closes the window.
            await r.Act("set-value", window, await r.Find(window, new { automationId = "NameBox" }), new { value = "미저장" });
            var close = Data(Ok(await r.Run("close-window", new { windowId = window, waitMilliseconds = 1500 })));
            Check(!close.GetProperty("closed").GetBoolean(), "window kept open by the prompt");
            var prompt = close.GetProperty("newWindows")[0];
            Equal("닫기 확인", prompt.GetProperty("title").GetString(), "prompt title");
            var promptId = prompt.GetProperty("windowId").GetString()!;
            await r.Act("invoke", promptId, await r.Find(promptId, new { controlType = "Button", nameStartsWith = "예" }));
            var gone = Data(Ok(await r.Run("wait-window", new { title = "WPF 검증 도구", absent = true }, 5000)));
            Check(gone.GetProperty("satisfied").GetBoolean(), "main window gone after answering the prompt");
        }),
        ("U04 Settings app opens through open, is found and read, and closes with the session", async () =>
        {
            await using var r = new Real(); await r.Ready();
            r.Session = Ok(await r.Call(new("session-open", MachineId: "ui-pc"))).ControlSessionId!;
            var opened = Data(Ok(await r.Run("open", new { target = "ms-settings:about", waitWindow = new { timeoutMilliseconds = 20000, className = "ApplicationFrameWindow" } }, 25000)));
            Check(opened.GetProperty("waitSatisfied").GetBoolean(), "settings window");
            var window = opened.GetProperty("window").GetProperty("windowId").GetString()!;
            JsonElement inspected = default;
            for (int i = 0; i < 20; i++) { inspected = Data(Ok(await r.Run("inspect", new { windowId = window, maxDepth = 12 }))); if (inspected.GetProperty("nodeCount").GetInt32() > 15) break; await Task.Delay(300); }
            Check(inspected.GetProperty("nodeCount").GetInt32() > 15, "settings content: " + inspected.GetProperty("tree").GetString());
            var closed = Ok(await r.Call(new("session-close", SessionId: r.Session))); r.Session = "";
            Check(Data(closed).GetProperty("closedWindowIds").GetArrayLength() + Data(closed).GetProperty("retainedWindowIds").GetArrayLength() >= 1, "window accounted for");
        }),
        ("U05 Control Panel opens through open and its items can be found", async () =>
        {
            await using var r = new Real(); await r.Ready();
            r.Session = Ok(await r.Call(new("session-open", MachineId: "ui-pc"))).ControlSessionId!;
            var opened = Data(Ok(await r.Run("open", new { target = "control.exe", waitWindow = new { timeoutMilliseconds = 15000, className = "CabinetWClass" } }, 20000)));
            Check(opened.GetProperty("waitSatisfied").GetBoolean(), "control panel window");
            var window = opened.GetProperty("window").GetProperty("windowId").GetString()!;
            JsonElement inspected = default;
            for (int i = 0; i < 20; i++) { inspected = Data(Ok(await r.Run("inspect", new { windowId = window, maxDepth = 10, interactiveOnly = true }))); if (inspected.GetProperty("nodeCount").GetInt32() > 10) break; await Task.Delay(300); }
            Check(inspected.GetProperty("nodeCount").GetInt32() > 10, "control panel content");
            var links = await r.Run("find-element", new { windowId = window, selector = new { controlType = "Hyperlink", index = 0 }, completeness = "realized" });
            Check(links.Status == "completed" || links.Error?.Code == "ElementNotFound", "hyperlink query " + links.Error?.Code);
        }),
        ("U06 6.4 elevation: runas without an elevated agent is refused before anything starts", async () =>
        {
            await using var r = new Real(); await r.Ready();
            r.Session = Ok(await r.Call(new("session-open", MachineId: "ui-pc"))).ControlSessionId!;
            var opened = await r.Run("open", new { target = "services.msc", verb = "runas" });
            if (Processes.CurrentElevated) Check(opened.Status == "completed", "elevated agent opens services.msc");
            else { Equal("rejected", opened.Status, "status"); Equal("ElevationRequired", opened.Error?.Code, "code"); }
        }),
    ];
}

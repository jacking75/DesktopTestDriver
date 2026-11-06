using DesktopTestDriver;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;

static class UiIntegrationTests
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    public static async Task<int> Run()
    {
        var root = Path.GetFullPath(Path.Combine("artifacts", "ui-tests", Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(root);
        int passed = 0, failed = 0; var cleanup = new List<object>();
        foreach (var fixture in new[] { "FormsWorkbench", "SimpleMemo" })
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", fixture, fixture + ".exe"));
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden }; start.ArgumentList.Add("--quiet");
            using var process = Process.Start(start) ?? throw new Exception("fixture start failed");
            using var fixtureJob=new ApplicationJob("ui-test-"+Guid.NewGuid().ToString("N"),process);
            DesktopProcess? identity = null; var foreground = GetForegroundWindow();
            try
            {
                identity = Processes.Identify(process);
                File.WriteAllText(Path.Combine(root, fixture+"-owned.json"),GuiProtocol.Encode(identity));
                using var current = Process.GetCurrentProcess();
                using var desktop = new WindowsDesktop(current.SessionId, Environment.ProcessPath!, root);
                if (!desktop.Valid) throw new GuiException("NoInteractiveSession");
                var store = new StateStore(Path.Combine(root, fixture)); var sessions = new ControlSessions(store, "fixture", current.SessionId, "dedicated");
                var input = new FakeInput(); var handler = new GuiRequestHandler(store, desktop, sessions, new GuiInput(store, input), machineUsage: "dedicated");
                var session = sessions.Open("control", 120, 2, "isolated-desktop", [identity]);
                async Task<GuiResponse> Request(string op, object args, string? requestId = null)
                {
                    var request = GuiProtocol.Parse(GuiProtocol.Encode(new { apiVersion = 2, requestId = requestId ?? Guid.NewGuid().ToString("N"), controlSessionId = session.ControlSessionId, operation = op, timeoutMilliseconds = 5000, arguments = args }));
                    var result = await handler.Execute(new(request, 5000));
                    if (result.Status != "completed") throw new Exception(GuiProtocol.Encode(result));
                    return result;
                }
                JsonElement Data(GuiResponse r) => JsonSerializer.SerializeToElement(r.Data, GuiProtocol.Json);
                string? window = null; var began = Stopwatch.GetTimestamp();
                while (window == null && Stopwatch.GetElapsedTime(began).TotalSeconds < 5)
                {
                    var found = Data(await Request("windows", new { })).GetProperty("windows");
                    if (found.GetArrayLength() > 0) window = found[0].GetProperty("windowId").GetString();
                    else await Task.Delay(50);
                }
                if (window == null) throw new Exception("fixture window missing");
                using var foregroundProbe = new ForegroundProbe(process.Id);
                var stages=new List<object>();
                void Stage(string name) { stages.Add(new { name,events=foregroundProbe.Count }); File.WriteAllText(Path.Combine(root,fixture+"-stages.json"),GuiProtocol.Encode(stages)); }
                Stage("ready");
                var tree = Data(await Request("inspect", new { windowId = window })).GetProperty("observation");
                File.WriteAllText(Path.Combine(root, fixture + "-tree.json"), tree.GetRawText());
                if (!tree.GetProperty("complete").GetBoolean() || tree.GetRawText().Contains("fixture-secret")) throw new Exception("incomplete or unredacted tree");
                async Task<JsonElement> Find(object selector) => Data(await Request("find-element", new { windowId = window, selector })).GetProperty("target");
                Stage("inspect");
                var editor = await Find(new { automationId = fixture == "FormsWorkbench" ? "Editor" : "MemoText" });
                var mutation = new { windowId = window, elementId = editor.GetProperty("elementId").GetString(), snapshotToken = editor.GetProperty("snapshotToken").GetString(), value = "한글\n독립 검증" };
                await Request("set-value", mutation, "set-editor"); await Request("set-value", mutation, "set-editor");
                Stage("set-value");
                var readBackNode = await Find(new { automationId = fixture == "FormsWorkbench" ? "Editor" : "MemoText" });
                var readBack = readBackNode.GetProperty("value").GetProperty("text").GetString()!;
                File.WriteAllText(Path.Combine(root, fixture + "-value.json"), GuiProtocol.Encode(new { requested = mutation.value, readBack }));
                if (readBack.Replace("\r\n", "\n", StringComparison.Ordinal) != mutation.value) throw new Exception("value readback mismatch");
                await Request("wait-condition", new { windowId = window, selector = new { automationId = fixture == "FormsWorkbench" ? "Editor" : "MemoText" }, condition = "value-equals", expected = readBack });
                Stage("value-wait");
                if (fixture == "FormsWorkbench")
                {
                    var apply = await Find(new { automationId = "Apply" });
                    await Request("invoke", new { windowId = window, elementId = apply.GetProperty("elementId").GetString(), snapshotToken = apply.GetProperty("snapshotToken").GetString() });
                    await Request("wait-condition", new { windowId = window, selector = new { automationId = "Result" }, condition = "value-equals", expected = readBack });
                    Stage("invoke-and-wait");
                    var disabled = await Find(new { automationId = "Disabled" });
                    if (disabled.GetProperty("enabled").GetBoolean()) throw new Exception("disabled state missing");
                    await Find(new { controlType = "Button", name = "이름 없는 버튼" });
                }
                var noGlobalInput = input.Events.Count == 0; var sameForeground = foregroundProbe.Count == 0;
                File.WriteAllText(Path.Combine(root, fixture + "-interference.json"), GuiProtocol.Encode(new { globalInputEvents = input.Events.Count, targetForegroundEvents = foregroundProbe.Count, providerReportedForegroundEvents = foregroundProbe.ReportedEvents, nativeForegroundPollMilliseconds = 5, unrelatedEvents = foregroundProbe.ForeignEvents, unrelatedForegroundEndpointChanged = foreground != GetForegroundWindow() }));
                if (!noGlobalInput || !sameForeground) throw new Exception("fixture interference observed");
                await handler.Release(sessions.Get(session.ControlSessionId), true, "UiTestEnd");
                passed++; Console.WriteLine("PASS UI " + fixture + " scoped UIA find/set/wait/duplicate, input=0, target foreground events=0");
            }
            catch (Exception e)
            {
                failed++;
                File.WriteAllText(Path.Combine(root, fixture + "-failure.json"), GuiProtocol.Encode(new { verdict = "FAIL", cause = "unconfirmed", message = e.Message }));
                Console.WriteLine("FAIL UI " + fixture + ": " + e.Message);
            }
            finally
            {
                var ended = identity != null ? Processes.KillOwned(identity) : process.HasExited;
                if (identity == null && !process.HasExited) { process.Kill(); ended = process.WaitForExit(1500); }
                cleanup.Add(new { fixture, identity, ended }); File.WriteAllText(Path.Combine(root, "cleanup.json"), GuiProtocol.Encode(cleanup));
                if (!ended) throw new Exception("fixture cleanup unconfirmed");
            }
        }
        File.WriteAllText(Path.Combine(root, "summary.json"), GuiProtocol.Encode(new { passed, failed, realDesktopInput = false, continuousForegroundMonitoring = true, policyHarness = "fixture-only", humanConcurrentUse = "NOT_RUN" }));
        Console.WriteLine("Evidence: " + root); return failed == 0 ? 0 : 1;
    }
}

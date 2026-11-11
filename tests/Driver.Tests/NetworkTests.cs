using System.Net.Sockets;
using System.Text.Json;
using DesktopTestDriver;
using static T;

/// <summary>Controller and agent hosts in this process, connected over plain TCP on a port in 25100-25199.</summary>
internal static class NetworkTests
{
    internal sealed class Hosts : IAsyncDisposable
    {
        public readonly ControllerSettings Controller;
        public readonly FakeDesktop Desktop = new();
        public readonly CancellationTokenSource Stop = new();
        private readonly Task controllerTask, agentTask;
        public readonly ControllerHost Host;
        public string Machine = "fake-pc";
        public Hosts()
        {
            var root = Root("network-tests");
            ControllerHost? host = null; Task? running = null;
            for (int attempt = 0; attempt < 20 && running == null; attempt++)
            {
                var port = 25100 + Random.Shared.Next(100);
                Controller = new ControllerSettings(Path.Combine(root, "controller-" + attempt), "127.0.0.1", port);
                try { using var probe = new TcpListener(System.Net.IPAddress.Loopback, port); probe.Start(); probe.Stop(); } catch (SocketException) { continue; }
                host = new ControllerHost(Controller); running = Task.Run(() => host.Run(Stop.Token));
            }
            Host = host!; controllerTask = running!;
            var agent = new AgentHost(new AgentSettings(Path.Combine(root, "agent"), Machine, "127.0.0.1", Controller!.Port), Desktop, new FakeInput());
            agentTask = Task.Run(() => agent.Run(Environment.ProcessPath!, Stop.Token, watchdog: false));
        }
        public async Task Ready()
        {
            for (int i = 0; i < 100; i++)
            {
                var status = await Call(new("status", MachineId: Machine));
                if (status.Status == "completed") return;
                await Task.Delay(50);
            }
            throw new Exception("agent did not connect");
        }
        public Task<GuiResponse> Call(LocalCommand command) => ControllerHost.Call(Controller, command);
        public Task<GuiResponse> Request(string session, string op, object args, string? id = null, int timeout = 5000) =>
            Call(new("request", Request: GuiProtocol.Encode(new GuiRequest(id ?? "r-" + Guid.NewGuid().ToString("N")[..12], session, op, timeout, JsonSerializer.SerializeToElement(args, GuiProtocol.Json)))));
        public async Task<(string Session, string Window)> Open(bool input = false)
        {
            var opened = Ok(await Call(new("session-open", MachineId: Machine) { Input = input, Targets = new() { Any = true } }));
            var session = opened.ControlSessionId!;
            var window = Data(Ok(await Request(session, "windows", new { }))).GetProperty("windows")[0].GetProperty("windowId").GetString()!;
            return (session, window);
        }
        public async Task<string> State(string session) => Data(Ok(await Call(new("session-status", SessionId: session)))).GetProperty("state").GetString()!;
        public async ValueTask DisposeAsync()
        {
            Stop.Cancel();
            try { await Task.WhenAll(controllerTask, agentTask).WaitAsync(TimeSpan.FromSeconds(15)); } catch { }
        }
    }
    public static IEnumerable<(string, Func<Task>)> All =>
    [
        ("N01 1.1 plain TCP: agent connects with only an address, session opens and runs", async () =>
        {
            await using var h = new Hosts(); await h.Ready();
            var (session, window) = await h.Open();
            var tree = Data(Ok(await h.Request(session, "inspect", new { windowId = window }))).GetProperty("tree").GetString()!;
            Check(tree.Contains("#MemoText"), tree);
            Ok(await h.Call(new("session-close", SessionId: session)));
        }),
        ("N02 3.1 a wait that times out through the controller leaves the session active", async () =>
        {
            await using var h = new Hosts(); await h.Ready(); var (session, window) = await h.Open();
            var r = await h.Request(session, "wait-condition", new { windowId = window, selector = new { automationId = "Missing" }, condition = "exists" }, timeout: 300);
            Equal("completed", r.Status, "status " + r.Error?.Code); Equal(false, Data(r).GetProperty("satisfied").GetBoolean(), "satisfied");
            Equal("active", await h.State(session), "session state");
            var find = await h.Request(session, "find-element", new { windowId = window, selector = new { automationId = "SaveButton" } });
            var target = Data(Ok(find)).GetProperty("target");
            Ok(await h.Request(session, "invoke", new { windowId = window, elementId = target.GetProperty("elementId").GetString(), snapshotToken = target.GetProperty("snapshotToken").GetString() }));
        }),
        ("N03 3.2 a pre-dispatch refusal through the controller is rejected; the session stays active", async () =>
        {
            await using var h = new Hosts(); await h.Ready(); var (session, window) = await h.Open();
            var target = Data(Ok(await h.Request(session, "find-element", new { windowId = window, selector = new { automationId = "Result" } }))).GetProperty("target");
            var r = await h.Request(session, "set-value", new { windowId = window, elementId = target.GetProperty("elementId").GetString(), snapshotToken = target.GetProperty("snapshotToken").GetString(), value = "x" });
            Equal("rejected", r.Status, "status"); Equal("ValueReadOnly", r.Error?.Code, "code"); Equal("active", await h.State(session), "session");
        }),
        ("N04 4.1 an oversized response is refused; the connection and other sessions survive", async () =>
        {
            await using var h = new Hosts(); await h.Ready();
            var (first, firstWindow) = await h.Open(); var (second, _) = await h.Open();
            h.Desktop.Trees[1001] = ("간단 메모", new FakeElement("42-1", "", new string('긴', 200_000), "Window"));
            var r = await h.Request(first, "inspect", new { windowId = firstWindow, format = "json" });
            Equal("ResponseTooLarge", r.Error?.Code, "code");
            Ok(await h.Call(new("status", MachineId: h.Machine)));
            Equal("active", await h.State(second), "other session");
        }),
        ("N05 4.2 a new connection with the same machine id replaces a half-open one", async () =>
        {
            await using var h = new Hosts(); await h.Ready();
            using var ghost = new TcpClient(); await ghost.ConnectAsync("127.0.0.1", h.Controller.Port);
            await GuiWire.Write(ghost.GetStream(), new AgentHello("ghost-pc", GuiProtocol.WireVersion), CancellationToken.None);
            Ok(await GuiWire.Read<GuiResponse>(ghost.GetStream(), CancellationToken.None));
            using var second = new TcpClient(); await second.ConnectAsync("127.0.0.1", h.Controller.Port);
            await GuiWire.Write(second.GetStream(), new AgentHello("ghost-pc", GuiProtocol.WireVersion), CancellationToken.None);
            Equal("completed", (await GuiWire.Read<GuiResponse>(second.GetStream(), CancellationToken.None)).Status, "reconnect accepted");
            // The silent connection is dropped after three seconds without a heartbeat.
            await Task.Delay(3800);
            var status = Data(Ok(await h.Call(new("status"))));
            Check(!status.GetProperty("agents").EnumerateArray().Any(a => a.GetProperty("machineId").GetString() == "ghost-pc"), "silent agent removed");
        }),
        ("N06 P9 capture: the controller saves the image (default folder) and the agent drops its copy", async () =>
        {
            await using var h = new Hosts(); await h.Ready(); var (session, window) = await h.Open();
            var r = Ok(await h.Request(session, "capture", new { windowId = window }));
            var path = Data(r).GetProperty("path").GetString()!;
            Check(File.Exists(path) && path.Contains("captures"), path);
            await Task.Delay(300);
            var agentRoot = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path))!)!, "agent");
            Equal(0, Directory.GetFiles(agentRoot, "cap-*.png").Length, "agent copy deleted after ack");
        }),
        ("N07 a mutation that times out during dispatch is completionUnknown and blocks; reads continue", async () =>
        {
            await using var h = new Hosts(); await h.Ready(); var (session, window) = await h.Open();
            var target = Data(Ok(await h.Request(session, "find-element", new { windowId = window, selector = new { automationId = "SaveButton" } }))).GetProperty("target");
            h.Desktop.MutationDelay = 2000;
            var r = await h.Request(session, "invoke", new { windowId = window, elementId = target.GetProperty("elementId").GetString(), snapshotToken = target.GetProperty("snapshotToken").GetString() }, timeout: 500);
            Equal("completionUnknown", r.Status, "a mutation that timed out during dispatch is unknown");
            Equal("blocked", await h.State(session), "blocked after unknown");
            h.Desktop.MutationDelay = 0;
            Ok(await h.Request(session, "inspect", new { windowId = window }));
            Equal("completionUnknown", (await h.Call(new("request-status", SessionId: session, RequestId: r.RequestId))).Status, "request-status");
        }),
        ("N08 10.5 trace export writes mutations with notes in order", async () =>
        {
            await using var h = new Hosts(); await h.Ready(); var (session, window) = await h.Open();
            var target = Data(Ok(await h.Request(session, "find-element", new { windowId = window, selector = new { automationId = "MemoText" } }))).GetProperty("target");
            var request = new GuiRequest("note-1", session, "set-value", 5000, JsonSerializer.SerializeToElement(new { windowId = window, elementId = target.GetProperty("elementId").GetString(), snapshotToken = target.GetProperty("snapshotToken").GetString(), value = "기록" }), "메모 입력");
            Ok(await h.Call(new("request", Request: GuiProtocol.Encode(request))));
            var output = Path.Combine(h.Controller.StateDirectory, "trace");
            Ok(await h.Call(new("trace-export", SessionId: session) { Output = output }));
            var line = File.ReadAllLines(Path.Combine(output, "trace.jsonl")).Single();
            Check(line.Contains("\"note\":\"메모 입력\"") && line.Contains("set-value"), line);
        }),
    ];
}

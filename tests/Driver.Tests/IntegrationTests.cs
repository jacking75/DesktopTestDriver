using DesktopTestDriver;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

static class IntegrationTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static JsonElement Data(GuiResponse response) => JsonSerializer.SerializeToElement(response.Data, GuiProtocol.Json);
    public static async Task<int> Run()
    {
        int passed = 0;
        string root = Path.GetFullPath(Path.Combine("artifacts", "network-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        using var allocator = new TcpListener(IPAddress.Loopback, 0); allocator.Start(); int port = ((IPEndPoint)allocator.LocalEndpoint).Port; allocator.Stop();
        var controller = new ControllerSettings(Path.Combine(root, "controller"), "127.0.0.1", port);
        var controllerStore = new StateStore(controller.StateDirectory); ConnectionSettings.Initialize(controllerStore, "controller");
        using var controllerCert = ConnectionSettings.Certificate(controllerStore);
        var aStore = new StateStore(Path.Combine(root, "a")); ConnectionSettings.Initialize(aStore, "A");
        var bStore = new StateStore(Path.Combine(root, "b")); ConnectionSettings.Initialize(bStore, "B");
        ConnectionSettings.Register(controllerStore, "A", aStore.PathFor("identity.cer")); ConnectionSettings.Register(controllerStore, "B", bStore.PathFor("identity.cer"));
        var a = new AgentSettings(aStore.Root, "A", "127.0.0.1", port, ConnectionSettings.Hash(controllerCert), 1);
        var b = new AgentSettings(bStore.Root, "B", "127.0.0.1", port, ConnectionSettings.Hash(controllerCert), 1);
        var desktopA = new FakeDesktop(); var desktopB = new FakeDesktop(); var inputA = new FakeInput(); var inputB = new FakeInput();
        using var stopController = new CancellationTokenSource(); using var stopAgents = new CancellationTokenSource();
        var hosting = Task.Run(() => new ControllerHost(controller).Run(stopController.Token));
        var agentA = Task.Run(() => new AgentHost(a, desktopA, inputA).Run(Environment.ProcessPath!, stopAgents.Token));
        var agentB = Task.Run(() => new AgentHost(b, desktopB, inputB).Run(Environment.ProcessPath!, stopAgents.Token));
        async Task<GuiResponse> Call(LocalCommand command) => await ControllerHost.Call(controller, command);
        async Task Wait(Func<Task<bool>> ready, int ms = 10000)
        {
            var began = Stopwatch.GetTimestamp(); while (!await ready()) { if (Stopwatch.GetElapsedTime(began).TotalMilliseconds > ms) throw new Exception("integration timeout"); await Task.Delay(30); }
        }
        async Task<GuiResponse> Request(string sid, string op, object arguments, string? id = null, int timeout = 5000)
        {
            return await Call(new("request", Request: GuiProtocol.Encode(new { apiVersion = 1, requestId = id ?? Guid.NewGuid().ToString("N"), controlSessionId = sid, operation = op, timeoutMilliseconds = timeout, arguments })));
        }
        try
        {
            await Wait(async () => { var status = await Call(new("status")); return status.Status == "completed" && Data(status).GetProperty("agents").GetArrayLength() == 2; });
            Check(!hosting.IsCompleted && !agentA.IsCompleted && !agentB.IsCompleted, "hosts exited");
            passed++; Console.WriteLine("PASS G02/G04 two authenticated Agent roles, internal watchdog processes");
            // A pinned certificate cannot claim B's registered machine identity.
            async Task<string> Impersonate(string machine, StateStore identity)
            {
                using var tcp = new TcpClient(); using var timeout = new CancellationTokenSource(5000); await tcp.ConnectAsync("127.0.0.1", port, timeout.Token);
                using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) => cert != null && ConnectionSettings.Hash(cert) == a.ControllerCertificate);
                using var cert = ConnectionSettings.Certificate(identity);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost", ClientCertificates = new X509CertificateCollection { cert }, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, timeout.Token);
                await GuiWire.Write(ssl, new AgentHello(machine, 1, "test", "Default"), timeout.Token);
                return (await GuiWire.Read<GuiResponse>(ssl, timeout.Token)).Error?.Code ?? "accepted";
            }
            Check(await Impersonate("B", aStore) == "MachineCertificateMismatch", "certificate identity mismatch accepted");
            Check(await Impersonate("A", aStore) == "DuplicateMachineConnection", "duplicate machine accepted");
            var rogue = new StateStore(Path.Combine(root, "rogue")); ConnectionSettings.Initialize(rogue, "rogue");
            bool rejected = false; try { await Impersonate("rogue", rogue); } catch (Exception e) when (e is IOException or AuthenticationException) { rejected = true; }
            Check(rejected, "unregistered certificate accepted"); passed++; Console.WriteLine("PASS G04 unknown certificate, name mismatch and duplicate connection rejected");
            var openA = await Call(new("session-open", MachineId: "A")); var openB = await Call(new("session-open", MachineId: "B", Mode: "observe"));
            Check(openA.Status == "completed" && openB.Status == "completed", "session open failed");
            var sidA = openA.ControlSessionId!; var sidB = openB.ControlSessionId!;
            try { _ = new ControllerHost(controller); throw new Exception("duplicate Controller host accepted"); } catch (GuiException e) { Check(e.Code == "AlreadyRunning", "unexpected duplicate Controller error"); }
            try { _ = new AgentHost(a, desktopA, inputA); throw new Exception("duplicate Agent host accepted"); } catch (GuiException e) { Check(e.Code == "AlreadyRunning", "unexpected duplicate Agent error"); }
            Check(aStore.Read<SessionInfo>("session-" + sidA + ".json").State == "active", "duplicate start rewrote live session recovery state");
            passed++; Console.WriteLine("PASS G03 duplicate starts are rejected before recovery touches live state");

            var windowsA = await Request(sidA, "windows", new { }); var windowsB = await Request(sidB, "windows", new { });
            var widA = Data(windowsA).GetProperty("windows")[0].GetProperty("windowId").GetString()!;
            var widB = Data(windowsB).GetProperty("windows")[0].GetProperty("windowId").GetString()!;
            var blocked = await Request(sidB, "focus", new { windowId = widA }); Check(blocked.Error?.Code == "ReadOnlySession" && desktopB.FocusCalls == 0, "observe side effect");
            var crossed = await Request(sidA, "focus", new { windowId = widB }); Check(crossed.Status == "rejected", "cross machine ID response: " + GuiProtocol.Encode(crossed));
            passed++; Console.WriteLine("PASS G05/G06 target routing and read-only Agent enforcement");
            var capturePath = Path.Combine(root, "capture.png"); var capId = "capture-request";
            var cap = await Request(sidB, "capture", new { windowId = widB, output = capturePath }, capId);
            Check(cap.Status == "completed" && File.Exists(capturePath) && Data(cap).GetProperty("sha256").GetString() == GuiProtocol.Hash(File.ReadAllBytes(capturePath)), "capture delivery failed: " + GuiProtocol.Encode(cap));
            var repeated = await Request(sidB, "capture", new { windowId = widB, output = capturePath }, capId);
            Check(repeated.Status == "completed" && desktopB.CaptureCalls == 1, "capture replayed or OutputExists replaced result");
            var exists = await Request(sidB, "capture", new { windowId = widB, output = capturePath }); Check(exists.Error?.Code == "OutputExists" && desktopB.CaptureCalls == 1, "capture overwrote existing output");
            var invalidOutput = Path.Combine(root, "missing-parent", "capture.png"); var bad = await Request(sidB, "capture", new { windowId = widB, output = invalidOutput });
            Check(bad.Status == "failed" && !File.Exists(invalidOutput), "missing local image reported completed");
            passed++; Console.WriteLine("PASS G07/G10 capture delivery, duplicate lookup before output check, delivery failure");
            var config = Path.Combine(root, "Controller.json"); File.WriteAllText(config, GuiProtocol.Encode(controller));
            var exe = Environment.GetEnvironmentVariable("DTD_TEST_CONTROLLER") ?? Path.GetFullPath("src/Controller/bin/Debug/net10.0-windows10.0.19041.0/Controller.exe");
            async Task<(int Code, string Output)> Cli(params string[] arguments)
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                psi.ArgumentList.Add("--config"); psi.ArgumentList.Add(config); foreach (var argument in arguments) psi.ArgumentList.Add(argument);
                using var process = Process.Start(psi)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)); return (process.ExitCode, await output);
            }
            var cliStatus = await Cli("--status"); using (var json = JsonDocument.Parse(cliStatus.Output)) Check(cliStatus.Code == 0 && json.RootElement.GetProperty("status").GetString() == "completed", "CLI stdout invalid");
            var wrong = await Cli("--status", "--unsupported"); using (var json = JsonDocument.Parse(wrong.Output)) Check(wrong.Code == 1 && json.RootElement.GetProperty("status").GetString() == "rejected", "CLI invalid options");
            passed++; Console.WriteLine("PASS G05 actual CLI processes emit one JSON and enforce arguments");
            // Kill only the short CLI after acceptance; the host must retain the result.
            desktopA.Entered.Reset(); desktopA.DelayControls = 600;
            var requestId = "lost-cli"; var file = Path.Combine(root, "request.json");
            File.WriteAllText(file, GuiProtocol.Encode(new { apiVersion = 1, requestId, controlSessionId = sidA, operation = "controls", arguments = new { windowId = widA } }));
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "--config", config, "--request", file }) start.ArgumentList.Add(argument);
            using (var cli = Process.Start(start)!) { Check(desktopA.Entered.Wait(3000), "CLI not accepted"); cli.Kill(); await cli.WaitForExitAsync(); }
            await Wait(async () => (await Call(new("request-status", SessionId: sidA, RequestId: requestId))).Status == "completed");
            Check(inputA.Events.Count == 0, "lost CLI caused input"); passed++; Console.WriteLine("PASS G07 actual CLI death preserves host-owned completion");
            desktopA.DelayControls = 0;
            // Only the relay is a stub. Controller routing, mTLS Agents, sessions,
            // durable input ownership and B read-only enforcement use production code.
            // This does not execute or validate RemoteAssist consent, transport or termination.
            bool simulatedRelayEnabled = false; int relayedClicks = 0; var relayedKeys = new HashSet<int>();
            inputA.OnTransition = (kind, code, down) =>
            {
                if (!simulatedRelayEnabled) return;
                if (kind == "button" && !down) relayedClicks++;
                if (kind == "unicode" && down) desktopB.ObservedValue += (char)code;
                if (kind == "key") { if (down) relayedKeys.Add(code); else relayedKeys.Remove(code); }
            };
            try
            {
                Check((await Request(sidA, "click", new { windowId = widA, automationId = "button" })).Status == "completed" && relayedClicks == 0, "disabled stub relayed input");
                simulatedRelayEnabled = true;
                Check((await Request(sidA, "click", new { windowId = widA, automationId = "button" })).Status == "completed" && relayedClicks == 1, "Viewer-side click routing failed");
                const string sample = "simulated 한글 😀";
                Check((await Request(sidA, "type-text", new { windowId = widA, text = sample })).Status == "completed", "Viewer-side text failed");
                Check((await Request(sidA, "key", new { windowId = widA, key = "F12", action = "down" })).Status == "completed" && relayedKeys.Contains(0x7B), "Viewer-side F12 down failed");
                Check((await Request(sidA, "release-input", new { })).Status == "completed" && relayedKeys.Count == 0, "Viewer-side independent release failed");
                var observed = Data(await Request(sidB, "controls", new { windowId = widB })).GetProperty("elements")[0].GetProperty("value").GetProperty("text").GetString();
                Check(observed == sample && inputB.Events.Count == 0 && desktopB.FocusCalls == 0, "B observation injected input or read wrong state");
                var denied = await Request(sidB, "type-text", new { windowId = widB, text = "forbidden" });
                Check(denied.Error?.Code == "ReadOnlySession" && desktopB.ObservedValue == sample && inputB.Events.Count == 0, "B observe bypassed read-only gate");
                simulatedRelayEnabled = false;
                Check((await Request(sidA, "click", new { windowId = widA, automationId = "button" })).Status == "completed" && relayedClicks == 1, "disabled stub changed target");
                passed++; Console.WriteLine("PASS G22 simulated Viewer A input and B observation contract (RemoteAssist NOT RUN)");
            }
            finally { inputA.OnTransition = null; desktopB.ObservedValue = ""; }
            var closed = await Call(new("session-close", SessionId: sidA)); Check(closed.Status == "completed", "close failed");
            var expiring = await Call(new("session-open", MachineId: "A", IdleSeconds: 1)); Check(expiring.Status == "completed", "idle open failed: " + GuiProtocol.Encode(expiring));
            await Task.Delay(1200); var expired = await Call(new("session-status", SessionId: expiring.ControlSessionId)); Check(expired.Status == "completed", "idle status failed: " + GuiProtocol.Encode(expired)); Check(Data(expired).GetProperty("state").GetString() == "expired", "status refreshed idle lifetime: " + GuiProtocol.Encode(expired));
            passed++; Console.WriteLine("PASS G06 idle expiration independent of result/status requests");
            var overdue = await Call(new("session-open", MachineId: "A")); var overdueWid = Data(await Request(overdue.ControlSessionId!, "windows", new { })).GetProperty("windows")[0].GetProperty("windowId").GetString()!;
            var held = await Request(overdue.ControlSessionId!, "key", new { windowId = overdueWid, key = "F12", action = "down" }); Check(held.Status == "completed", "down failed");
            await Task.Delay(1800); Check(aStore.Read<HeldInput[]>("held-input.json").Length == 0, "max hold not released");
            passed++; Console.WriteLine("PASS G15 mock held-input maximum independent of CLI lifetime");
            Check(Processes.KillOwned(aStore.Read<DesktopProcess>("watchdog-process.json")), "watchdog not owned");
            await agentA.WaitAsync(TimeSpan.FromSeconds(6));
            Check(aStore.Read<HeldInput[]>("held-input.json").Length == 0, "watchdog death left input");
            Check((await Request(sidB, "windows", new { })).Status == "completed", "other Agent lost after watchdog death");
            passed++; Console.WriteLine("PASS G15 watchdog death stops Agent input; other machine continues observation");
            // Simulate a host dying before it durably receives the Agent's final capture response.
            var recorded = new RequestJournal(controllerStore); var entry = recorded.Find(sidB, capId)!;
            recorded.Save(entry with { Response = entry.Response with { Status = "completionUnknown" }, AgentResponse = null, ExpectedHash = null, ExpectedBytes = null });
            stopController.Cancel(); await hosting.WaitAsync(TimeSpan.FromSeconds(8));
            using var restartedStop = new CancellationTokenSource();
            var restarted = Task.Run(() => new ControllerHost(controller).Run(restartedStop.Token));
            try
            {
                await Wait(async () => { var status = await Call(new("status", MachineId: "B")); return status.Status == "completed"; });
                int capturesBefore = desktopB.CaptureCalls;
                var recovered = await Call(new("request-status", SessionId: sidB, RequestId: capId));
                Check(recovered.Status == "completed" && Data(recovered).GetProperty("path").GetString() == capturePath && desktopB.CaptureCalls == capturesBefore, "restart replayed or lost verified image");
                passed++; Console.WriteLine("PASS G07/G10 Controller restart reconciles original Agent receipt and local hash without recapture");
            }
            finally { restartedStop.Cancel(); await restarted.WaitAsync(TimeSpan.FromSeconds(8)); }


        }
        finally
        {
            stopAgents.Cancel();
            try { await Task.WhenAll(agentA, agentB).WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception e) { Console.Error.WriteLine("Agent shutdown: " + e.GetType().Name); }
            stopController.Cancel(); await hosting.WaitAsync(TimeSpan.FromSeconds(10));
        }
        return passed;
    }
}

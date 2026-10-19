using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DesktopTestDriver;

internal static class ControllerTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, Wire.Json);
    private static int Port()
    {
        for (var port = 25170; port <= 25199; port++)
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            try { listener.Start(); return port; }
            catch (SocketException) { }
            finally { listener.Stop(); }
        }
        throw new Exception("No free development port");
    }
    public static async Task Commands()
    {
        using var f = new Fixture(frameAge: 5000, heartbeat: 1000);
        var root = Path.Combine(f.Parent, "controller");
        ControllerFiles.Initialize(root, "127.0.0.1", Port());
        var connection = ControllerFiles.Enroll(root, f.Store.PathFor("run.json"));
        using var stop = new CancellationTokenSource(20000);
        using var clientStop = new CancellationTokenSource();
        var server = new ControllerAgent(root).Serve(stop.Token);
        var identity = Processes.Identify(Process.GetCurrentProcess());
        var status = new ClientStatus(identity, identity, 1, "Default", f.Store.ManifestHash, f.Store.PipeName);
        var client = ClientController.Run(f.Store, f.Engine, status, connection, clientStop.Token);
        var cleanup = Cleanup(f, clientStop);
        try
        {
            await Tests.Until(() => File.Exists(f.Store.PathFor("controller-connected.json")), 3000);
            async Task<Result> Request(object arguments)
            {
                var reply = await ControllerAgent.Call(root, new("request", connection.RunId, Json(arguments)), stop.Token);
                Tests.Check(reply.Ok, "local API accepted");
                return Wire.Decode<Result>(Wire.Encode(reply.Data));
            }
            var inspected = await Request(new { name = "inspect" });
            Tests.Check(inspected.Verified, "actual TCP inspect reply");
            var observation = Wire.Decode<InspectResult>(Wire.Encode(inspected.Evidence));
            var frame = observation.Frames.Single();
            var typed = await Request(new { name = "text", text = "aaa", expectedWindow = frame.Window, frameId = frame.Id });
            Tests.Check(typed.Executed && typed.ErrorCode == Error.None && !typed.Verified && f.Backend.Events.Count(e => e == "down:unicode:97") == 3, "text travels over TCP, application not falsely verified");
            // A duplicate hello cannot replace or disconnect an established peer.
            using (var duplicate = new TcpClient())
            {
                await duplicate.ConnectAsync(connection.Host, connection.Port, stop.Token);
                using var channel = new ControllerChannel(duplicate.GetStream());
                await channel.Write(new("hello", Hello: connection, Status: status), stop.Token);
                bool refused = false;
                try { await channel.Read(stop.Token); } catch (DriverException) { refused = true; }
                Tests.Check(refused, "duplicate peer refused");
            }
            f.Desktop.Observation = f.Desktop.Observation with { Foreground = 0 };
            var waiting = Request(new { name = "wait", condition = "foreground", timeoutMilliseconds = 5000, expectedWindow = frame.Window });
            var initial = f.Store.Read<long>("server-heartbeat");
            await Tests.Until(() => f.Store.Read<long>("server-heartbeat") > initial, 1500);
            var stopped = await Request(new { name = "stop" });
            Tests.Check(stopped.Verified, "stop bypassed pending wait");
            await waiting;
            await client.WaitAsync(TimeSpan.FromSeconds(4)); await cleanup;
            ControllerReply outcome = new(false);
            var began = Stopwatch.GetTimestamp();
            do
            {
                outcome = await ControllerAgent.Call(root, new("result", connection.RunId), stop.Token);
                if (!outcome.Ok) await Task.Delay(20);
            } while (!outcome.Ok && Stopwatch.GetElapsedTime(began).TotalSeconds < 2);
            Tests.Check(outcome.Ok && Json(outcome.Data!).GetProperty("cleanup").GetProperty("clean").GetBoolean(), "cleanup returned on TCP");
        }
        finally { f.Store.Stop(Error.Stopped); clientStop.Cancel(); stop.Cancel(); await client; await cleanup; await server; }
    }
    private static async Task Cleanup(Fixture f, CancellationTokenSource clientStop)
    {
        await Tests.Until(() => f.Store.Stopped, 15000);
        var clean = f.Input.Cleanup();
        f.Store.Write("cleanup.json", new { clean, normalDesktop = true, ledger = f.Store.Read<InputLedger>("ledger.json") });
        clientStop.Cancel();
    }
    public static async Task Disconnect()
    {
        using var f = new Fixture(heartbeat: 1000);
        var root = Path.Combine(f.Parent, "controller");
        ControllerFiles.Initialize(root, "127.0.0.1", Port());
        var connection = ControllerFiles.Enroll(root, f.Store.PathFor("run.json"));
        using var stop = new CancellationTokenSource(10000);
        using var clientStop = new CancellationTokenSource();
        var server = new ControllerAgent(root).Serve(stop.Token);
        var identity = Processes.Identify(Process.GetCurrentProcess());
        var client = ClientController.Run(f.Store, f.Engine, new(identity, identity, 1, "Default", f.Store.ManifestHash, f.Store.PipeName), connection, clientStop.Token);
        try
        {
            await Tests.Until(() => File.Exists(f.Store.PathFor("controller-connected.json")), 3000);
            stop.Cancel(); await server;
            await Tests.Until(() => f.Store.Stopped, 2000);
            Tests.Check(f.Store.Stopped, "EOF stops client");
        }
        finally { f.Store.Stop(Error.Stopped); clientStop.Cancel(); stop.Cancel(); await client; await server; }
    }
    public static async Task Applications()
    {
        using var f = new Fixture(applications: true);
        using var apps = new OwnedApplications();
        bool rejected = false;
        try { apps.Launch(f.Store, "not-enrolled", () => { }); } catch (DriverException) { rejected = true; }
        Tests.Check(rejected && f.Store.Read<InputLedger>("ledger.json").Processes.Length == 0, "unknown application rejected");
        var identity = apps.Launch(f.Store, "idle", () => { });
        await Tests.Until(() => Processes.Alive(identity), 1000);
        Tests.Check(f.Store.Read<InputLedger>("ledger.json").Processes.Single().Identity == identity, "durable owned process");
        Tests.Check(f.Input.Cleanup() && !Processes.Alive(identity), "job and process cleaned");
        Tests.Check(f.Store.Read<InputLedger>("ledger.json").Processes.Length == 0, "empty process ledger");
    }
    public static async Task Unsigned()
    {
        using var f = new Fixture();
        var root = Path.Combine(f.Parent, "controller");
        ControllerFiles.Initialize(root, "127.0.0.1", Port());
        var setup = Path.Combine(f.Parent, "unsigned"); RunStore.CreatePrivateDirectory(setup);
        var configPath = Path.Combine(setup, "config.json");
        var config = f.Store.Config with { RunId = "unsigned-run", Delegation = f.Store.Config.Delegation! with { Signature = "" },
            Windows = [new(f.Window.ImagePath, f.Window.ImageHash, "*")] };
        File.WriteAllText(configPath, Wire.Encode(config));
        var connection = ControllerFiles.Enroll(root, configPath);
        File.WriteAllText(Path.Combine(setup, "controller.json"), Wire.Encode(connection));
        var store = RunStore.Create(Path.Combine(setup, "state"), configPath);
        using var engine = new CommandEngine(store, f.Desktop, new InputController(store, f.Backend), () => true);
        var inspect = f.Command("inspect") with { RunId = config.RunId, ManifestHash = store.ManifestHash };
        var observed = await engine.Execute(inspect);
        var frame = Wire.Decode<InspectResult>(Wire.Encode(observed.Evidence)).Frames.Single();
        f.Desktop.Observation = f.Desktop.Observation with { Windows = [f.Window with { Title = "changed by typing" }] };
        var typed = await engine.Execute(f.Command("text") with { RunId = config.RunId, ManifestHash = store.ManifestHash, Text = "aaa", ExpectedWindow = frame.Window, FrameId = frame.Id });
        Tests.Check(typed.Executed && typed.ErrorCode == Error.None, "explicit wildcard tolerates title changes, not identity changes");
        File.WriteAllText(store.PathFor("controller.json"), Wire.Encode(connection with { MachineId = "wrong" }));
        bool rejected = false;
        try { _ = new RunStore(store.Root); } catch (DriverException) { rejected = true; }
        Tests.Check(rejected, "wrong routing config rejected");
    }
}

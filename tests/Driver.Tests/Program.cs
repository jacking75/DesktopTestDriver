using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using DesktopTestDriver;

if (Environment.GetEnvironmentVariable("DTD_TEST_FIXTURE_MODE") is { } fixtureMode)
{
    if (fixtureMode == "hang") { await Task.Delay(30000); return 0; }
    using var request = System.Text.Json.JsonDocument.Parse((await Lines.Read(Console.In, default))!);
    var packet = request.RootElement;
    Console.WriteLine(Wire.Encode(new FixtureReceipt(packet.GetProperty("runId").GetString()!, packet.GetProperty("fixtureId").GetString()!,
        packet.GetProperty("stepId").GetString()!, true, fixtureMode != "real", new string('a', 64)))); return 0;
}

if (Environment.GetEnvironmentVariable("DTD_TEST_CONSOLE_MODE") is { } consoleMode)
{
    if (consoleMode == "hang") { await Task.Delay(30000); return 0; }
    using var request = System.Text.Json.JsonDocument.Parse((await Lines.Read(Console.In, default))!);
    var machine = request.RootElement.GetProperty("machineId").GetString()!;
    var run = request.RootElement.GetProperty("runId").GetString()!;
    Console.WriteLine(Wire.Encode(new RecoveryReceipt(machine, run, true, true, true, true, true, new string('a',64)))); return 0;
}
if (args is ["--idle"]) { await Task.Delay(30000); return 0; }
if (args is ["--watchdog", var state])
{
    var store = new RunStore(state);
    return await WatchdogRunner.Run(store, store.Read<ProcessIdentity>("client.json"), new JournalInput(state), () => true) ? 0 : 1;
}
if (args is ["--client-worker", var path])
{
    var store = new RunStore(path);
    store.Write("client.json", Processes.Identify(Process.GetCurrentProcess())); store.Pulse("client-heartbeat");
    using var watchdog = Processes.Start(Environment.ProcessPath!, "--watchdog", path);
    var guard = Processes.Identify(watchdog);
    await Tests.Until(() => File.Exists(store.PathFor("watchdog.json")), 2000);
    var input = new InputController(store, new JournalInput(path));
    input.Down("key", 65);
    store.Write("worker-ready.json", guard);
    while (!store.Stopped && Processes.Alive(guard)) { store.Pulse("client-heartbeat"); await Task.Delay(25); }
    store.Stop(Error.WatchdogUnavailable);
    bool clean = input.Cleanup(); store.Write("worker-cleanup.json", new { clean });
    return clean ? 0 : 1;
}

var cases = new (string Name, Func<Task> Run)[]
{
    ("external scenario contract rejects unsafe grammar and preserves typed references", ScriptScenarioTests.Contract),
    ("external scenario polls observations, binds data and persists cleanup evidence", ScriptScenarioTests.Lifecycle),
    ("external scenario first failure, ambiguity and cancellation always enter cleanup", ScriptScenarioTests.FailureCleanup),
    ("TCP controller commands, independent heartbeat, stop and cleanup use no SSH", ControllerTests.Commands),
    ("TCP controller shutdown stops the connected client", ControllerTests.Disconnect),
    ("application launch is owned from creation and cleaned without desktop input", ControllerTests.Applications),
    ("trusted TCP run accepts unsigned config and checks routing binding", ControllerTests.Unsigned),
    ("wire rejects duplicates, unknown fields and oversized messages", Tests.WireValidation),
    ("private run state rejects reuse and authenticates a real local pipe", Tests.Ipc),
    ("frame scope, negative monitor origin and Unicode input stay independent", Tests.InputOperations),
    ("command replay never injects twice and changed payload is rejected", Tests.Replay),
    ("durable pre-down intent survives partial injection and failed release", Tests.PartialInput),
    ("physical held keys, key allowlist and scope are fail closed", Tests.InputDenials),
    ("stale frame, DPI change, HWND reuse, desktop and foreground changes are blocked", Tests.Preconditions),
    ("metadata frames focus only the exact approved background window", Tests.Focus),
    ("stop preempts wait, heartbeat does not queue, lease replay does not refresh", Tests.StopAndHeartbeat),
    ("expired, rebound and repeated sequence commands are rejected", Tests.Envelopes),
    ("atomic reservations retain expired and unclean runs across restart", Tests.Reservation),
    ("multi-client barriers and first failure stop all nodes once", Tests.MultiClient),
    ("monotonic deadline, lease and held-key caps expire independently", Tests.Deadlines),
    ("killing a local fake client triggers independent process recovery", Tests.KillClient),
    ("killing a local watchdog triggers fake client release", Tests.KillWatchdog),
    ("owned process cleanup verifies creation time, path and hash", Tests.OwnedProcess),
    ("R2 adapter mocks cannot authorize real UAC, lock or reboot", Tests.Recovery),
    ("external console process validates executable hash and terminates hung adapters", Tests.ExternalConsole),
    ("owner signature, scope denial, redacted audit and evidence retention are enforced", ExtensionTests.AuthorizationPolicy),
    ("physical input always stops while delegated sealed injections remain observable", ReviewTests.InputClassification),
    ("concurrent heartbeat replacement and transient frames do not fail evidence accounting", ReviewTests.ConcurrentEvidence),
    ("owned fixture lifecycle and independent scenario use no OS input in local tests", ReviewTests.DesktopLifecycle),
    ("fixture observation rejects stale, replaced, incomplete and stuck-input evidence", ReviewTests.DesktopEvidence),
    ("external fixture executable pin, receipt identity and timeout are enforced", ReviewTests.ExternalFixture),
    ("long-load fixture samples through its minimum duration and stops on first failure", ReviewTests.LongLoad),
    ("scenario engine binds provenance, stops on first failure and refuses false PASS", ExtensionTests.Scenarios),
    ("fixture failure restores baseline and real environment gates reject mock proof", ExtensionTests.Fixtures),
    ("coverage, evidence bundle and preparation keep simulated work out of acceptance", ExtensionTests.AcceptanceAndPreparation),
};
int passed = 0;
foreach (var test in cases)
{
    var started = Stopwatch.GetTimestamp();
    try { await test.Run(); Console.WriteLine($"PASS {test.Name} ({Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms)"); passed++; }
    catch (Exception e) { Console.Error.WriteLine($"FAIL {test.Name}: {e}"); return 1; }
}
Console.WriteLine(Wire.Encode(new { passed, failed = 0, realDesktopInput = false, twoPc = false }));
return 0;

internal static class Tests
{
    public static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static async Task Until(Func<bool> condition, int milliseconds)
    {
        var start = Stopwatch.GetTimestamp();
        while (!condition()) { if (Stopwatch.GetElapsedTime(start).TotalMilliseconds > milliseconds) throw new TimeoutException("test readiness"); await Task.Delay(10); }
    }
    public static void Reject(Action action, Error expected)
    {
        try { action(); } catch (DriverException e) when (e.Code == expected) { return; }
        throw new Exception("expected rejection " + expected);
    }
    public static Task WireValidation()
    {
        Reject(() => Wire.Decode<Inventory>("{\"protocolVersion\":1,\"ProtocolVersion\":1,\"machines\":[]}"), Error.InvalidRequest);
        bool unknown = false; try { Wire.Decode<Inventory>("{\"protocolVersion\":1,\"machines\":[],\"surprise\":true}"); } catch (System.Text.Json.JsonException) { unknown = true; }
        Check(unknown, "unknown field accepted");
        Reject(() => Wire.Decode<Inventory>(new string('x', Wire.MaxMessage + 1)), Error.InvalidRequest);
        using var f = new Fixture();
        Reject(() => (f.Store.Config with { Recovery = RecoveryGrade.R0 }).Validate(), Error.RecoveryRequired);
        Reject(() => (f.Store.Config with { Windows = [new("relative", new string('a', 64), "x")] }).Validate(), Error.InvalidRequest);
        return Task.CompletedTask;
    }
    public static async Task Ipc()
    {
        using var f = new Fixture();
        Check(!f.Store.Authenticate("wrong") && f.Store.Authenticate(File.ReadAllText(f.Store.PathFor("secret"))), "secret check");
        Reject(() => RunStore.Create(f.Store.Root, Path.Combine(f.Parent, "source.json")), Error.ReservationConflict);
        using var cancellation = new CancellationTokenSource(4000);
        var serving = PipeRelay.Serve(f.Store, f.Engine, cancellation.Token);
        var command = f.Command("inspect");
        var reply = await PipeRelay.Send(f.Store, command, cancellation.Token);
        Check(reply.ErrorCode == Error.None && reply.Verified, "real named pipe command failed");
        using (var bad = new NamedPipeClientStream(".", f.Store.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await bad.ConnectAsync(cancellation.Token);
            using var writer = new StreamWriter(bad, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            using var reader = new StreamReader(bad, Encoding.UTF8, false, 4096, true);
            await writer.WriteLineAsync(Wire.Encode(new AuthRequest("wrong", f.Command("inspect"))));
            Check(await Lines.Read(reader, cancellation.Token) == null, "unauthenticated IPC returned data");
        }
        cancellation.Cancel(); await serving;
    }
    public static async Task InputOperations()
    {
        using var f = new Fixture(); var frame = await f.Snapshot();
        var move = await f.Engine.Execute(f.Command("move") with { FrameId = frame.Id, X = 10, Y = 20 });
        Check(move.ErrorCode == Error.None && !move.Verified && move.Executed && f.Backend.Events.Contains("move:-1790:120"), "negative origin or PASS semantics");
        foreach (var command in new[] {
            f.Command("click") with { FrameId = frame.Id, X = 1, Y = 2 },
            f.Command("wheel") with { FrameId = frame.Id, X = 1, Y = 2, Delta = -120 },
            f.Command("drag") with { FrameId = frame.Id, X = 1, Y = 2, EndX = 20, EndY = 30, Steps = 4 },
            f.Command("chord") with { FrameId = frame.Id, Keys = [17,65] },
            f.Command("text") with { FrameId = frame.Id, Text = "한🙂" },
            f.Command("key-down") with { FrameId = frame.Id, Keys = [66] },
            f.Command("key-up") with { FrameId = frame.Id, Keys = [66] } })
            Check((await f.Engine.Execute(command)).ErrorCode == Error.None, "input operation failed " + command.Name);
        Check(f.Store.Read<InputLedger>("ledger.json").Held.Length == 0, "keys leaked");
        Check(f.Backend.Events.Count(x => x.StartsWith("down:unicode:")) == 3, "Unicode UTF16 sequence lost");
    }
    public static async Task Replay()
    {
        using var f = new Fixture(); var frame = await f.Snapshot();
        var click = f.Command("click") with { FrameId = frame.Id, X = 1, Y = 1 };
        var result = await f.Engine.Execute(click); var count = f.Backend.Events.Count;
        Check(ReferenceEquals(result, await f.Engine.Execute(click)), "original result not returned");
        Check(f.Backend.Events.Count == count, "duplicate injected");
        Check((await f.Engine.Execute(click with { X = 2 })).ErrorCode == Error.DuplicateMismatch, "changed replay accepted");
        Check((await f.Engine.Execute(f.Command("click") with { FrameId = frame.Id, Sequence = click.Sequence })).ErrorCode == Error.SequenceRejected, "sequence replay accepted");
    }
    public static Task PartialInput()
    {
        using var f = new Fixture(); f.Backend.FailDown = 1;
        Reject(() => f.Input.Down("key", 65), Error.InjectionFailed);
        Check(f.Store.Read<InputLedger>("ledger.json").Held is [{ Code: 65 }], "pre-injection intent lost");
        f.Backend.FailUp = 1; Check(!f.Input.ReleaseAll(), "partial release reported clean");
        Check(f.Store.Read<InputLedger>("ledger.json").Held.Length == 1, "failed release erased");
        Check(f.Input.ReleaseAll(), "independent release failed");
        var count = f.Backend.Events.Count; Check(f.Input.ReleaseAll() && count == f.Backend.Events.Count, "release not idempotent");
        return Task.CompletedTask;
    }
    public static async Task InputDenials()
    {
        using (var f = new Fixture())
        {
            f.Backend.PhysicalDown = true;
            Reject(() => f.Input.Down("key", 65), Error.UserInputConflict);
            Check(f.Store.Read<InputLedger>("ledger.json").Held.Length == 0, "physical key claimed");
        }
        using (var f = new Fixture())
        {
            var frame = await f.Snapshot();
            Check((await f.Engine.Execute(f.Command("key-down") with { FrameId = frame.Id, Keys = [91] })).ErrorCode == Error.DelegationMismatch, "unapproved key accepted");
            Check(f.Store.Stopped && f.Backend.Events.Count == 0, "denial didn't seal");
        }
        using (var f = new Fixture())
        {
            var frame = await f.Snapshot(); f.Desktop.Conflict = true;
            Check((await f.Engine.Execute(f.Command("click") with { FrameId = frame.Id })).ErrorCode == Error.UserInputConflict, "physical conflict ignored");
        }
    }
    public static async Task Preconditions()
    {
        foreach (var change in new[] { "dpi", "foreground", "pid", "session", "desktop", "bounds", "stale" })
        {
            using var f = new Fixture(frameAge: change == "stale" ? 50 : 1500); var frame = await f.Snapshot();
            switch (change)
            {
                case "dpi": f.Desktop.Observation = f.Desktop.Observation with { Monitors = [f.Desktop.Observation.Monitors[0] with { DpiX = 192 }] }; break;
                case "foreground": f.Desktop.Observation = f.Desktop.Observation with { Foreground = 321 }; break;
                case "pid": f.Desktop.Observation = f.Desktop.Observation with { Windows = [f.Window with { Pid = 9999 }] }; break;
                case "session": f.Desktop.Observation = f.Desktop.Observation with { Session = 2 }; break;
                case "desktop": f.Desktop.Observation = f.Desktop.Observation with { Desktop = "Winlogon" }; break;
                case "bounds": f.Desktop.Observation = f.Desktop.Observation with { Windows = [f.Window with { Bounds = f.Window.Bounds with { X = 0 } }] }; break;
                case "stale": await Task.Delay(70); break;
            }
            var result = await f.Engine.Execute(f.Command("click") with { FrameId = frame.Id });
            Check(result.ErrorCode != Error.None && f.Backend.Events.Count == 0 && f.Store.Stopped, "precondition bypass " + change);
        }
    }
    public static async Task Focus()
    {
        using var f = new Fixture(); f.Desktop.Observation = f.Desktop.Observation with { Foreground = 999 };
        Check((await f.Engine.Execute(f.Command("wait") with { Condition = "window" })).Verified, "background window wait required focus");
        var inspect = await f.Engine.Execute(f.Command("inspect"));
        using var doc = System.Text.Json.JsonDocument.Parse(Wire.Encode(inspect.Evidence));
        var frame = Wire.Decode<Frame>(doc.RootElement.GetProperty("frames")[0].GetRawText());
        var result = await f.Engine.Execute(f.Command("focus") with { FrameId = frame.Id });
        Check(result.ErrorCode == Error.None && f.Desktop.Observation.Foreground == f.Window.Hwnd, "focus metadata unusable");
    }
    public static async Task StopAndHeartbeat()
    {
        using var f = new Fixture(); var frame = await f.Snapshot();
        var waiting = f.Engine.Execute(f.Command("wait") with { FrameId = frame.Id, Condition = "frame-change", TimeoutMilliseconds = 5000 });
        await Task.Delay(30);
        var pulse = f.Command("heartbeat");
        var heartbeat = await f.Engine.Execute(pulse); var tick = f.Store.Read<long>("server-heartbeat");
        Check(heartbeat.ErrorCode == Error.None, "heartbeat queued behind wait");
        await f.Engine.Execute(pulse);
        Check(f.Store.Read<long>("server-heartbeat") == tick, "replayed lease was renewed");
        var before = Stopwatch.GetTimestamp();
        var stop = await f.Engine.Execute(f.Command("stop"));
        Check(stop.Verified && Stopwatch.GetElapsedTime(before).TotalMilliseconds < 500, "stop queued behind wait");
        var interrupted = await waiting;
        Check(interrupted.ErrorCode == Error.Stopped, "wait not interrupted: " + interrupted.ErrorCode);
        Check((await f.Engine.Execute(f.Command("release-all") with { Deadline = DateTimeOffset.UtcNow.AddMinutes(-1) })).Verified, "expired cleanup blocked");
    }
    public static async Task Envelopes()
    {
        using var f = new Fixture();
        Check((await f.Engine.Execute(f.Command("inspect") with { ManifestHash = new string('b', 64) })).ErrorCode == Error.DelegationMismatch, "manifest mismatch");
        Check((await f.Engine.Execute(f.Command("inspect") with { Deadline = DateTimeOffset.UtcNow.AddSeconds(-1) })).ErrorCode == Error.Expired, "expired envelope");
        var frame = await f.Snapshot(); f.WatchdogAlive = false;
        Check((await f.Engine.Execute(f.Command("key-down") with { FrameId = frame.Id, Keys = [65] })).ErrorCode == Error.WatchdogUnavailable, "dead watchdog input accepted");
    }
    public static Inventory Inventory()
    {
        var recovery = new RecoverySettings(RecoveryGrade.R0, "unavailable", null, null, null);
        var ssh = new SshSettings("unused.invalid", 22, "lab", "ssh-ed25519", Convert.ToBase64String(new byte[32]), "password-env", "DTD_UNUSED");
        return new(Wire.Version, [new("a", "vm", "owner", "windows11", "mock", ["mock"], ["host","viewer"], "VM\\lab", "C:/Driver", ssh, recovery),
            new("b", "vm", "owner", "windows11", "mock", ["mock"], ["host","viewer"], "VM\\lab", "C:/Driver", ssh, recovery)]);
    }
    public static async Task Reservation()
    {
        using var f = new Fixture(); var reservation = new Reservations(Path.Combine(f.Parent, "reservations")); var inventory = Inventory();
        var hash = new string('a', 64);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => Task.Run(() =>
        {
            try { reservation.Acquire(inventory, "run-" + i, [new("a","host"),new("b","viewer")], hash, hash, DateTimeOffset.UtcNow.AddSeconds(5)); return true; }
            catch (DriverException e) when(e.Code == Error.ReservationConflict) { return false; }
        })));
        Check(outcomes.Count(x => x) == 1, "machine double-booked");
        var owned = reservation.Read("a"); Check(reservation.Read("b").RunId == owned.RunId, "partial reservation leak");
        Reject(() => reservation.Complete("a", owned.RunId, false), Error.CleanupIncomplete);
        Reject(() => reservation.Complete("a", "different-run", true), Error.CleanupIncomplete);
        // Expired reservation still blocks reuse until explicit verified cleanup.
        File.WriteAllText(Path.Combine(reservation.Root,"a.json"), Wire.Encode(owned with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }));
        Reject(() => new Reservations(reservation.Root).Acquire(inventory, "new", [new("a","viewer")], hash, hash, DateTimeOffset.UtcNow.AddSeconds(2)), Error.ReservationConflict);
        reservation.Complete("a", owned.RunId, true); reservation.Complete("b", owned.RunId, true);
        Reject(() => (inventory with { Machines = [inventory.Machines[0], inventory.Machines[0]] }).Validate(), Error.InvalidRequest);
    }
    public static async Task MultiClient()
    {
        var a = new FakeNode("a"); var b = new FakeNode("b");
        await using (var campaign = new Campaign([a,b], TimeSpan.FromSeconds(2)))
        {
            var hash = new string('a',64);
            await campaign.Barrier("ready", ["a","b"], [Task.FromResult(new BarrierEvidence("a","ready",true,hash)),Task.FromResult(new BarrierEvidence("b","ready",true,hash))], TimeSpan.FromMilliseconds(100));
            b.Failure = Error.InjectionFailed;
            using var f = new Fixture(); await campaign.Execute("b", f.Command("inspect"));
            Check(campaign.IsStopped && a.Stops == 1 && b.Stops == 1 && await campaign.Stop(), "global stop missing");
        }
        a = new("a"); b = new("b");
        await using (var campaign = new Campaign([a,b], TimeSpan.FromSeconds(2)))
        {
            bool failed = false;
            try { await campaign.Barrier("ready", ["a","b"], [Task.FromResult(new BarrierEvidence("a","ready",true,new string('a',64))), new TaskCompletionSource<BarrierEvidence>().Task], TimeSpan.FromMilliseconds(40)); }
            catch (DriverException e) when(e.Code == Error.BarrierFailed) { failed = true; }
            Check(failed && a.Stops == 1 && b.Stops == 1, "barrier deadline did not stop all");
        }
        a = new("a"); b = new("b") { Clean = false };
        await using (var campaign = new Campaign([a,b], TimeSpan.FromMilliseconds(60)))
        {
            await Until(() => campaign.IsStopped, 500);
            Check(!await campaign.Stop(), "unclean node reported PASS");
        }
    }
    public static Task Deadlines()
    {
        using var f = new Fixture(); var now = Stopwatch.GetTimestamp(); var config = f.Store.Config;
        long Ago(int ms) => now - Stopwatch.Frequency * ms / 1000;
        var policy = new WatchdogPolicy(config, now);
        Check(policy.Evaluate(now, Ago(config.HeartbeatMilliseconds + 1), now, true, true, new([],[])) == Error.TransportLost, "client heartbeat bound");
        Check(policy.Evaluate(now, now, Ago(config.HeartbeatMilliseconds + 1), true, true, new([],[])) == Error.TransportLost, "server lease bound");
        Check(policy.Evaluate(now, now, now, true, true, new([new("key",65,Ago(config.MaxHoldMilliseconds))],[])) == Error.Expired, "hold cap");
        Check(new WatchdogPolicy(config, Ago(config.MaxSeconds*1000)).Evaluate(now, now, now, true, true, new([],[])) == Error.Expired, "monotonic global deadline");
        return Task.CompletedTask;
    }
    public static async Task KillClient()
    {
        using var f = new Fixture(heartbeat: 5000);
        using var client = Processes.Start(Environment.ProcessPath!, "--idle");
        var clientId = Processes.Identify(client);
        f.Store.Write("client.json", clientId); f.Store.Pulse("client-heartbeat"); f.Store.Pulse("server-heartbeat");
        f.Input.Down("key",65);
        using var watchdog = Processes.Start(Environment.ProcessPath!, "--watchdog", f.Store.Root);
        try
        {
            await Until(() => File.Exists(f.Store.PathFor("watchdog.json")),2000);
            Check(Processes.KillOwned(clientId), "fake client kill failed");
            await watchdog.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Check(watchdog.ExitCode == 0 && f.Store.Read<InputLedger>("ledger.json").Held.Length == 0 &&
                File.ReadAllText(f.Store.PathFor("recovery.txt")).Contains("up:key:65"), "independent recovery did not release");
        }
        finally { if(!client.HasExited) Processes.KillOwned(clientId); if(!watchdog.HasExited) watchdog.Kill(); }
    }
    public static async Task KillWatchdog()
    {
        using var f = new Fixture(heartbeat:5000);
        using var client = Processes.Start(Environment.ProcessPath!,"--client-worker",f.Store.Root);
        var id = Processes.Identify(client); ProcessIdentity? watchdog = null;
        try
        {
            await Until(() => File.Exists(f.Store.PathFor("worker-ready.json")),3000);
            watchdog = f.Store.Read<ProcessIdentity>("worker-ready.json");
            Check(Processes.KillOwned(watchdog), "fake watchdog kill failed");
            // Observe release directly; process teardown and file flushing are separate from key recovery.
            // Keep both waits bounded instead of assuming all three finish within one 3s scheduling window.
            await Until(() => File.Exists(f.Store.PathFor("worker-cleanup.json")) && f.Store.Read<InputLedger>("ledger.json").Held.Length == 0,5000);
            await client.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Check(client.ExitCode == 0 && f.Store.Read<InputLedger>("ledger.json").Held.Length == 0 &&
                File.ReadAllText(f.Store.PathFor("recovery.txt")).Contains("up:key:65"), "client recovery failed");
        }
        finally { if(!client.HasExited) Processes.KillOwned(id); if(watchdog != null && Processes.Alive(watchdog)) Processes.KillOwned(watchdog); }
    }
    public static Task OwnedProcess()
    {
        using var f = new Fixture(); using var process = Processes.Start(Environment.ProcessPath!,"--idle"); var id = Processes.Identify(process);
        try
        {
            Check(!Processes.KillOwned(id with { ImageHash = new string('b',64) }) && !process.HasExited, "mismatched process killed");
            f.Store.Write("ledger.json",new InputLedger([], [new(id)]));
            Check(f.Input.Cleanup() && process.WaitForExit(1000), "owned process not cleaned");
        }
        finally { if(!process.HasExited) Processes.KillOwned(id); }
        return Task.CompletedTask;
    }
    public static async Task Recovery()
    {
        var machine = Inventory().Machines[0]; var adapter = new MockRecovery();
        var receipt = await RecoveryGate.Recover(adapter,machine.Id,"run",TimeSpan.FromMilliseconds(100));
        Check(receipt.Simulated && receipt.InputReleased && receipt.ResourcesClean, "mock recovery path");
        foreach (var scenario in new[]{"uac","lock","sas","reboot","os-hang"})
            Reject(() => RecoveryGate.Require(RecoveryGate.RequiredFor(scenario),machine,receipt),Error.RecoveryRequired);
        Reject(() => new UnsupportedRecoveryAdapter().Probe("a",default),Error.RecoveryRequired);
        var timeout = false;
        try { await RecoveryGate.Recover(new MockRecovery { Hang = true },"a","run",TimeSpan.FromMilliseconds(30)); } catch(OperationCanceledException) { timeout = true; }
        Check(timeout,"unbounded external console call");
    }
    public static async Task ExternalConsole()
    {
        var previous = Environment.GetEnvironmentVariable("DTD_TEST_CONSOLE_MODE");
        try
        {
            Environment.SetEnvironmentVariable("DTD_TEST_CONSOLE_MODE","reply");
            var config = new ConsoleAdapterConfig(Environment.ProcessPath!, Wire.FileHash(Environment.ProcessPath!), "vm-console",2000);
            var adapter = new ExternalConsoleAdapter(config);
            var receipt = await adapter.Probe("a",default);
            Check(receipt.Simulated && receipt.MachineId == "a", "external JSON probe lost binding");
            bool rejected = false;
            try { await new ExternalConsoleAdapter(config with { ExecutableHash = new string('b',64) }).Probe("a",default); }
            catch(DriverException e) when(e.Code == Error.RecoveryRequired) { rejected = true; }
            Check(rejected,"unreviewed console executable launched");
            Environment.SetEnvironmentVariable("DTD_TEST_CONSOLE_MODE","hang");
            bool expired = false;
            try { await new ExternalConsoleAdapter(config with { TimeoutMilliseconds = 100 }).Recover("a","run",default); }
            catch(OperationCanceledException) { expired = true; }
            Check(expired,"hung console executable not stopped");
        }
        finally { Environment.SetEnvironmentVariable("DTD_TEST_CONSOLE_MODE",previous); }
    }
    private sealed class MockRecovery : IRecoveryAdapter
    {
        public bool Hang;
        public string Kind => "mock";
        public Task<RecoveryReceipt> Probe(string machineId,CancellationToken cancellation) => Recover(machineId,"run",cancellation);
        public Task<RecoveryReceipt> Recover(string machineId,string run,CancellationToken cancellation) => Hang ? new TaskCompletionSource<RecoveryReceipt>().Task : Task.FromResult(new RecoveryReceipt(machineId,run,true,true,true,true,true,new string('a',64)));
    }
}

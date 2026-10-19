using System.Diagnostics;
using System.Text;
using DesktopTestDriver;

internal static class ReviewTests
{
    public static async Task ConcurrentEvidence()
    {
        using var f = new Fixture();
        var writer = Task.Run(() =>
        {
            for (int i=0; i<150; i++)
            {
                f.Store.Pulse("server-heartbeat");
                f.Store.Write("frame-transient.png", new { i });
                File.Delete(f.Store.PathFor("frame-transient.png"));
            }
        });
        try { for (int i=0; i<300; i++) { EvidenceVault.Budget(f.Store); await Task.Yield(); } }
        finally { await writer; }
        Tests.Check(!f.Store.Stopped,"accounting marked healthy run stopped");
        using (var file = new FileStream(f.Store.PathFor("committed.bin"),FileMode.CreateNew,FileAccess.Write))
            file.SetLength(f.Store.Config.Delegation!.Evidence.MaxBytes);
        Tests.Reject(() => EvidenceVault.Budget(f.Store),Error.Expired);
    }
    public static async Task InputClassification()
    {
        var own = InputConflictPolicy.DriverMarker;
        Tests.Check(InputConflictPolicy.IsConflict(false, own, true), "physical input ignored because of marker/seal");
        Tests.Check(!InputConflictPolicy.IsConflict(true, own, false), "driver injection rejected");
        Tests.Check(InputConflictPolicy.IsConflict(true, UIntPtr.Zero, false), "unsealed external injection accepted");
        Tests.Check(!InputConflictPolicy.IsConflict(true, UIntPtr.Zero, true), "sealed product injection rejected");
        using var f = new Fixture();
        Tests.Check(!InputConflictPolicy.ExternalInputAllowed(f.Store), "unsealed permission accepted");
        Tests.Check((await f.Engine.Execute(f.Command("seal-input") with { Condition = "self-test" })).Verified, "seal failed");
        Tests.Check(InputConflictPolicy.ExternalInputAllowed(f.Store), "delegated injection unavailable");
        Tests.Check((await f.Engine.Execute(f.Command("resume-input") with { Condition = "self-test" })).Verified && !InputConflictPolicy.ExternalInputAllowed(f.Store), "resume left injection allowed");
        using var invalid = new Fixture();
        Tests.Check((await invalid.Engine.Execute(invalid.Command("seal-input") with { Condition = "unsigned-scenario" })).ErrorCode == Error.DelegationMismatch, "unsigned seal accepted");
    }
    public static async Task DesktopLifecycle()
    {
        using var f = new Fixture(heartbeat: 5000, ownedFixture: true);
        var backend = new SampleInput(f.Store); var launcher = new SampleLauncher(f, backend);
        var engine = new CommandEngine(f.Store, f.Desktop, new InputController(f.Store, backend), () => true, launcher);
        var node = new EngineNode(f.Store, engine, backend); var ui = new NodeUi(node, f.Store.Config, f.Store.ManifestHash);
        var registry = new AdapterRegistry(); registry.Register(new DesktopFixtureAdapter(ui, f.Store.Config, _ => Task.FromResult(true)));
        try
        {
            var context = new ScenarioContext(f.Store.Config.RunId, new('a', 64), new('b', 64), f.Store.ManifestHash, [f.Store.Config], true);
            var report = await new ScenarioRunner(registry).Run(DesktopFixturePlan.Create("host"), context);
            Tests.Check(report.Verdict == Verdict.Pass && report.Simulated && report.CleanupVerified, "independent scenario failed: " + Wire.Encode(report));
            Tests.Check(launcher.Count == 1 && !Processes.Alive(launcher.Identity!) && f.Store.Read<InputLedger>("ledger.json").Processes.Length == 0, "owned fixture survived cleanup");
            Tests.Check(report.Steps.Last().Evidence.Single().Source == "fixture-observer", "injection receipt substituted for observation");
        }
        finally { new InputController(f.Store, backend).Cleanup(); }

        using var denied = new Fixture(ownedFixture: true);
        var bad = denied.Store.Config with { Fixture = denied.Store.Config.Fixture! with { ImageHash = new('f',64) } };
        Tests.Reject(bad.Validate, Error.DelegationMismatch);
        using var killed = new Fixture(heartbeat: 5000, ownedFixture: true);
        var b = new SampleInput(killed.Store); var l = new SampleLauncher(killed, b);
        var e = new CommandEngine(killed.Store, killed.Desktop, new InputController(killed.Store,b), () => true,l);
        try
        {
            Tests.Check((await e.Execute(killed.Command("fixture-start"))).Verified, "fixture launch failed");
            var held = killed.Store.Read<InputLedger>("ledger.json"); Tests.Check(held.Processes.Length == 1, "process not ledgered");
            Tests.Check((await e.Execute(killed.Command("fixture-start"))).ErrorCode == Error.ReservationConflict && l.Count == 1 && !Processes.Alive(l.Identity!), "duplicate launch or error cleanup failed");
        }
        finally { new InputController(killed.Store,b).Cleanup(); }
    }
    public static Task DesktopEvidence()
    {
        using var f = new Fixture(); var identity = Processes.Identify(Process.GetCurrentProcess()); var tick = Stopwatch.GetTimestamp();
        var before = new FixtureSample("run", DateTimeOffset.UtcNow, tick, tick, identity, 1, 123, true, 0, 0, 0, "", [], true, false, [], new(1,1));
        var after = before with { Tick = tick + 3, Clicks = 1, Drag = 2, Wheel = 1, Text = DesktopFixturePlan.Text,
            Events = [new(tick+1,"key",16,"down"),new(tick+2,"key",16,"up")] };
        var facts = DesktopFixtureAdapter.Judge(before,after);
        Tests.Check(facts["text-matches"] == "true" && facts["keys-released"] == "true", "fixture evidence rejected");
        Tests.Check(DesktopFixtureAdapter.Judge(before,after with { HeldVirtualKeys = [16] })["keys-released"] == "false", "stuck key passed");
        Tests.Check(DesktopFixtureAdapter.Judge(before,after with { Events = [after.Events[0]] })["keys-released"] == "false", "missing up passed");
        Tests.Reject(() => DesktopFixtureAdapter.Judge(before,after with { Identity = identity with { Pid = 123456 } }), Error.ObservationUnavailable);
        Tests.Reject(() => DesktopFixtureAdapter.Judge(before,after with { Utc = DateTimeOffset.UtcNow.AddSeconds(-3) }), Error.ObservationUnavailable);
        Tests.Reject(() => DesktopFixtureAdapter.Judge(before,after with { ObservationOverflow = true }), Error.ObservationUnavailable);
        using var guard = new Fixture(heartbeat: 5000, ownedFixture: true);
        guard.Store.Write("client.json",identity); guard.Store.Write("watchdog.json",identity); guard.Store.Pulse("client-heartbeat");
        Tests.Check(OwnedFixture.MayRun(guard.Store,identity,Stopwatch.GetTimestamp()), "pre-registration guard rejected live owner");
        guard.Store.Write("client-heartbeat",Stopwatch.GetTimestamp()-Stopwatch.Frequency*6);
        Tests.Check(!OwnedFixture.MayRun(guard.Store,identity,Stopwatch.GetTimestamp()), "orphan kept running after heartbeat loss");
        guard.Store.Pulse("client-heartbeat"); guard.Store.Stop(Error.Stopped);
        Tests.Check(!OwnedFixture.MayRun(guard.Store,identity,Stopwatch.GetTimestamp()), "orphan ignored durable stop");
        var plan = ExtensionTests.Plan();
        var report = new ScenarioReport("run",plan.Id,plan.Hash,new('a',64),new('b',64),new('c',64),Verdict.Pass,true,true,1,[]);
        var bundle = Path.Combine(f.Parent,"bundle-with-audit"); EvidenceBundle.Write(bundle,plan,report);
        var raw = Path.Combine(bundle,"client-evidence"); RunStore.CreatePrivateDirectory(raw);
        var audit = Path.Combine(raw,"audit.jsonl"); File.WriteAllText(audit,"{\"phase\":\"delegated\"}\n");
        EvidenceBundle.Complete(bundle,plan,report); EvidenceBundle.Verify(bundle);
        File.AppendAllText(audit,"tampered");
        Tests.Reject(() => EvidenceBundle.Verify(bundle),Error.DelegationMismatch);
        return Task.CompletedTask;
    }
    public static async Task ExternalFixture()
    {
        var previous = Environment.GetEnvironmentVariable("DTD_TEST_FIXTURE_MODE");
        var definition = Wire.Decode<FixtureDefinition>(File.ReadAllText(Path.Combine(ExtensionTests.Root(),"fixtures/generic-desktop.json")));
        var config = new FixtureBackendConfig(Environment.ProcessPath!, Wire.FileHash(Environment.ProcessPath!), "review-run",
            new() { [definition.Id] = Wire.Hash(Encoding.UTF8.GetBytes(Wire.Encode(definition))) }, definition.Environments, RecoveryGrade.R1, true, 1000);
        try
        {
            Environment.SetEnvironmentVariable("DTD_TEST_FIXTURE_MODE","normal");
            var receipt = await new ExternalFixtureBackend(config).Execute(definition,definition.Steps[0],config.RunId,default);
            Tests.Check(receipt.Verified && receipt.Simulated, "external fixture did not execute");
            bool rejected = false;
            try { await new ExternalFixtureBackend(config with { ExecutableHash = new('f',64) }).Execute(definition,definition.Steps[0],config.RunId,default); }
            catch (DriverException d) when (d.Code == Error.DelegationMismatch) { rejected = true; }
            Tests.Check(rejected,"changed executable accepted");
            rejected = false;
            try { await new ExternalFixtureBackend(config with { Simulated = false }).Execute(definition,definition.Steps[0],config.RunId,default); }
            catch (DriverException d) when (d.Code == Error.ObservationUnavailable) { rejected = true; }
            Tests.Check(rejected,"simulated receipt promoted");
            Environment.SetEnvironmentVariable("DTD_TEST_FIXTURE_MODE","hang");
            var expired = false;
            try { await new ExternalFixtureBackend(config with { TimeoutMilliseconds = 50 }).Execute(definition,definition.Steps[0],config.RunId,default); }
            catch (OperationCanceledException) { expired = true; }
            Tests.Check(expired,"hung external fixture survived timeout");
        }
        finally { Environment.SetEnvironmentVariable("DTD_TEST_FIXTURE_MODE",previous); }
    }
    public static async Task LongLoad()
    {
        var fixture = new FixtureDefinition("load","bounded load",RecoveryGrade.R1,["mock-equipment"],false,7200,
            [new("start","start-load","started"),new("metrics","collect-metrics","measured")],[new("restore","restore-baseline","restored")],7800);
        var clock = new AdvancingClock(); var backend = new LoadBackend(clock);
        var report = await FixtureRunner.Run(fixture,"load-run",backend,false,fixture.Environments,_ => Task.FromResult(true),timeProvider:clock);
        Tests.Check(report.Verdict == Verdict.Pass && backend.Samples >= 2 && report.Receipts.Length == 3 && report.Restored, "minimum load interval not sampled");
        backend = new LoadBackend(new AdvancingClock()) { Fail = true };
        report = await FixtureRunner.Run(fixture,"load-run",backend,false,fixture.Environments,_ => Task.FromResult(true),timeProvider:backend.Clock);
        Tests.Check(report.Verdict == Verdict.Fail && backend.Samples == 1 && report.Restored, "load continued after failure");
    }
    private sealed class AdvancingClock : TimeProvider
    {
        public long Tick;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Tick;
    }
    private sealed class LoadBackend(AdvancingClock clock) : IFixtureBackend
    {
        public AdvancingClock Clock => clock;
        public int Samples;
        public bool Fail;
        public Task<FixtureReceipt> Execute(FixtureDefinition fixture, FixtureStep step, string runId, CancellationToken cancellation)
        {
            if (step.Operation == "collect-metrics") { Samples++; clock.Tick += 7200; }
            return Task.FromResult(new FixtureReceipt(runId,fixture.Id,step.Id,!Fail || step.Operation != "collect-metrics",false,new('a',64)));
        }
    }
    private sealed class EngineNode(RunStore store, CommandEngine engine, IInputBackend backend) : IClientNode
    {
        public string MachineId => store.Config.MachineId;
        public Task<Result> Execute(Command command,CancellationToken cancellation) => engine.Execute(command);
        public Task<bool> Stop(CancellationToken cancellation) { store.Stop(Error.Stopped); return Task.FromResult(new InputController(store,backend).Cleanup()); }
    }
    private sealed class SampleLauncher(Fixture fixture, SampleInput input) : IFixtureLauncher
    {
        public int Count;
        public ProcessIdentity? Identity;
        public Process Start(FixtureScope scope,RunStore store,int seconds)
        {
            Count++; var process = Processes.Start(Environment.ProcessPath!,"--idle"); Identity = Processes.Identify(process);
            var w = fixture.Window with { Pid = Identity.Pid, CreatedAt = Identity.CreatedAt, ImagePath = Identity.ImagePath, ImageHash = Identity.ImageHash };
            fixture.Desktop.Observation = fixture.Desktop.Observation with { Windows = [w], Foreground = w.Hwnd };
            var tick = Stopwatch.GetTimestamp();
            input.Sample = new(store.Config.RunId,DateTimeOffset.UtcNow,tick,tick,Identity,1,w.Hwnd,true,0,0,0,"",[],true,false,[],new(1,1));
            input.Save(); return process;
        }
    }
    private sealed class SampleInput(RunStore store) : IInputBackend
    {
        public FixtureSample Sample = null!;
        private bool heldButton;
        public bool IsDown(string kind,int code) => false;
        public void Save() => store.Write("fixture-input.json",Sample with { Tick = Stopwatch.GetTimestamp(), Utc = DateTimeOffset.UtcNow });
        public void Down(string kind,int code)
        {
            if (kind == "unicode") Sample = Sample with { Text = Sample.Text + (char)code };
            else { Sample = Sample with { Events = [.. Sample.Events,new(Stopwatch.GetTimestamp(),kind,code,"down")] }; if (kind == "button") heldButton=true; }
            Save();
        }
        public void Up(string kind,int code)
        {
            if (kind != "unicode") Sample = Sample with { Events = [.. Sample.Events,new(Stopwatch.GetTimestamp(),kind,code,"up")] };
            if (kind == "button") { Sample = Sample with { Clicks = Sample.Clicks+1 }; heldButton=false; }
            Save();
        }
        public void Move(int x,int y) { if (heldButton) Sample = Sample with { Drag = Sample.Drag+1 }; Save(); }
        public void Wheel(int delta) { Sample = Sample with { Wheel = Sample.Wheel+1 }; Save(); }
    }
}

using DesktopTestDriver;

internal static class ExtensionTests
{
    public static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName,"DesktopTestDriver.slnx"))) return dir.FullName;
        throw new Exception("independent tool root missing");
    }
    public static async Task Fixtures()
    {
        var paths = new[] { Path.Combine(Root(),"fixtures","generic-desktop.json") };
        foreach (var path in paths)
        {
            var fixture = Wire.Decode<FixtureDefinition>(File.ReadAllText(path)); fixture.Validate();
            var pass = await FixtureRunner.Run(fixture,"mock-run",new SimulatedFixtureBackend(),true,fixture.Environments,_ => Task.FromResult(false));
            Tests.Check(pass.Verdict == Verdict.Pass && pass.Restored && pass.Simulated,"mock fixture failed");
            var fail = await FixtureRunner.Run(fixture,"mock-run",new SimulatedFixtureBackend { FailStep = fixture.Steps[0].Id },true,fixture.Environments,_ => Task.FromResult(false));
            Tests.Check(fail.Verdict == Verdict.Fail && fail.Restored && fail.Receipts.Length == 1 + fixture.Restore.Length,"first fixture failure continued");
            var blocked = await FixtureRunner.Run(fixture,"mock-run",new SimulatedFixtureBackend(),false,fixture.Environments,_ => Task.FromResult(false));
            Tests.Check(blocked.Verdict == Verdict.Blocked && blocked.Receipts.Length == 0,"real recovery bypassed");
            var dirty = await FixtureRunner.Run(fixture,"mock-run",new SimulatedFixtureBackend { FailStep = fixture.Restore[0].Id },true,fixture.Environments,_ => Task.FromResult(false));
            Tests.Check(dirty.Verdict == Verdict.Fail && !dirty.Restored,"cleanup failure passed");
        }
    }
    public static async Task AcceptanceAndPreparation()
    {
        using var f = new Fixture(); var plan = Plan(); var context = Context(f); var registry = new AdapterRegistry(); registry.Register(new CounterAdapter());
        var report = await new ScenarioRunner(registry).Run(plan,context);
        var output = Path.Combine(f.Parent,"bundle"); EvidenceBundle.Write(output,plan,report); EvidenceBundle.Verify(output);
        File.AppendAllText(Path.Combine(output,"report.json")," "); Tests.Reject(() => EvidenceBundle.Verify(output),Error.DelegationMismatch);
        var requirement = new AcceptanceCase("F01","two PC",[plan.Id],["two-pc"],["all-subconditions"]);
        Tests.Check(AcceptanceGate.Evaluate(requirement,[report],context.CandidateHash,["two-pc"],new Dictionary<string,bool>{{"all-subconditions",true}},2) == Verdict.NotRun,"mock report promoted");
        Tests.Check(AcceptanceGate.Evaluate(requirement,[report with { Simulated=false }],context.CandidateHash,["two-pc"],new Dictionary<string,bool>{{"all-subconditions",true}},1) == Verdict.Blocked,"single PC promoted");
        var proc = Processes.Identify(System.Diagnostics.Process.GetCurrentProcess());
        var status = new ClientStatus(proc,proc,1,"Default",f.Store.ManifestHash,"mock");
        var observed = new PreparedMachine("test",status,f.Desktop.Observation,new("test",f.Store.Config.RunId,true,true,true,true,true,new('a',64)));
        var ready = Preparation.Check(plan,context,[new(f.Store.PathFor("run.json"),f.Store.ManifestHash)],[observed],f.Store.Root);
        Tests.Check(ready.ContractsReady && !ready.ReadyForRealExecution && ready.Simulated,"preparation mock promoted");
        var fail = Preparation.Check(plan,context,[new(f.Store.PathFor("run.json"),new('f',64))],[observed],f.Store.Root);
        Tests.Check(!fail.ContractsReady,"changed candidate accepted");
    }
    public static ScenarioPlan Plan(string adapter = "counter-example") => new("self-test", adapter, "제품 없는 시나리오 판정", ["D14"], ["모의 노드"], ["독립 관측"],
        "첫 실패 즉시 종료", "소유 입력·자원 해제와 복귀", "계약 검사 재사용", 10, 20, 0, RecoveryGrade.R0,
        [new("one", "host", "increment", [], [new("count", "eq", "1", "sample-observer", "host")]),
         new("two", "host", "increment", [], [new("count", "eq", "2", "sample-observer", "host")])]);
    public static ScenarioContext Context(Fixture f) => new(f.Store.Config.RunId, new('a', 64), new('b', 64), f.Store.ManifestHash, [f.Store.Config], true);
    public static async Task Scenarios()
    {
        using var f = new Fixture(); var registry = new AdapterRegistry(); var adapter = new CounterAdapter(); registry.Register(adapter);
        var plan = Plan(); var context = Context(f);
        Tests.Check(Wire.Decode<ScenarioPlan>(Wire.Encode(plan)).Hash == plan.Hash, "plan roundtrip drift");
        var report = await new ScenarioRunner(registry).Run(plan, context);
        Tests.Check(report.Verdict == Verdict.Pass && report.Simulated && report.CleanupVerified && adapter.Cleaned && report.Steps.Length == 2, "sample failed");
        var failing = plan with { Steps = [plan.Steps[0] with { Assertions = [new("count", "eq", "9", "sample-observer", "host")] }, plan.Steps[1]] };
        var failingContext = context with { Machines = [f.ForPlan(failing)] };
        report = await new ScenarioRunner(registry).Run(failing, failingContext);
        Tests.Check(report.Verdict == Verdict.Fail && report.Steps.Length == 1 && report.CleanupVerified, "first failure continued");
        var wrongSource = new Assertion("count", "eq", "1", "product-remote", "host");
        Tests.Check(!ResultJudge.Matches(wrongSource, report.Steps[0].Evidence), "local action passed as product input");
        var evidence = report.Steps[0].Evidence[0];
        Tests.Reject(() => ResultJudge.Bind(evidence with { CandidateHash = new('f',64) }, failing.Steps[0], failing, context), Error.ObservationUnavailable);
        Tests.Reject(() => (plan with { Steps = [plan.Steps[0] with { Assertions = [] }] }).Validate(), Error.InvalidRequest);
        Tests.Reject(() => context.Validate(failing),Error.DelegationMismatch);
        Tests.Check(!ResultJudge.Matches(new("count", "ge", "1", "sample-observer", "host"), [evidence with { Facts = new() { ["count"] = "NaN" } }]), "NaN passed");
    }
    public static async Task AuthorizationPolicy()
    {
        using var f = new Fixture();
        Tests.Reject(() => Authorization.Verify(f.Store.Config with { MachineId = "different" }), Error.AuthenticationFailed);
        Tests.Reject(() => Authorization.Verify(f.Store.Config with { Delegation = null }), Error.DelegationMismatch);
        var restricted = f.Store.Config with { Delegation = f.Store.Config.Delegation! with { Actions = ["inspect"] } };
        Tests.Reject(() => Authorization.Require(restricted, "click"), Error.DelegationMismatch);
        Tests.Reject(() => Authorization.Require(f.Store.Config, "inspect", "outside-scenario"), Error.DelegationMismatch);
        Authorization.Require(restricted, "stop");
        var frame = await f.Snapshot();
        var text = "private-PIN-password-한글";
        var result = await f.Engine.Execute(f.Command("text") with { FrameId = frame.Id, Text = text });
        Tests.Check(result.ErrorCode == Error.None, "authorized text rejected");
        Tests.Check(!File.ReadAllText(f.Store.PathFor("audit.jsonl")).Contains(text), "audit leaked text");
        Tests.Reject(() => EvidenceVault.PurgeExpired(f.Store, DateTimeOffset.UtcNow.AddDays(2)), Error.CleanupIncomplete);
        await f.Engine.Execute(f.Command("stop"));
        Tests.Check(EvidenceVault.PurgeExpired(f.Store, DateTimeOffset.UtcNow) == 0, "retention ended early");
        Tests.Check(EvidenceVault.PurgeExpired(f.Store, DateTimeOffset.UtcNow.AddDays(2)) > 0 && File.Exists(f.Store.PathFor("ledger.json")) && File.Exists(f.Store.PathFor("secret")), "purge damaged recovery state");
        using var full = new Fixture(); var latest = await full.Snapshot();
        using (var log = new FileStream(full.Store.PathFor("audit.jsonl"),FileMode.Open,FileAccess.Write)) log.SetLength(4_194_305);
        var denied = await full.Engine.Execute(full.Command("click") with { FrameId = latest.Id });
        Tests.Check(denied.ErrorCode == Error.Expired && full.Backend.Events.Count == 0 && full.Store.Stopped,"audit capacity failure allowed input");
        Tests.Check((await full.Engine.Execute(full.Command("stop"))).Verified,"audit failure blocked emergency cleanup");
    }
}

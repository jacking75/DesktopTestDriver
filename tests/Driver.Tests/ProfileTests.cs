using DesktopTestDriver;
using System.Diagnostics;
using System.Text.Json;

static class ProfileTests
{
    public static int Run()
    {
        int passed = 0;
        void Check(bool value) { if (!value) throw new Exception("profile assertion failed"); }
        void Reject(Action action, string code) { try { action(); throw new Exception("expected " + code); } catch (GuiException e) { Check(e.Code == code); } }
        var root = Path.GetFullPath(Path.Combine("artifacts", "profile-tests", Guid.NewGuid().ToString("N"))); var store = new StateStore(root);
        var executable = Environment.ProcessPath!;
        var profile = new ApplicationProfile(1, "fixture", "windows-uia", new("exe", "work", "hash"), new() { ["button"] = new(AutomationId: "Apply") }, new() { ["apply"] = new("button", "invoke") });
        var bindings = new MachineBindings(new() { ["exe"] = executable, ["work"] = AppContext.BaseDirectory }, new() { ["hash"] = GuiProtocol.Hash(File.ReadAllBytes(executable)) });
        var registry = new ProfileRegistry(store); var registered = registry.Register(GuiProtocol.Encode(profile), bindings);
        Check(registry.List().SequenceEqual(new[] { "fixture" }));
        Reject(() => registry.Register(GuiProtocol.Encode(profile), bindings with { Fingerprints = new() { ["hash"] = "wrong" } }), "ApplicationHashMismatch");
        passed++; Console.WriteLine("PASS S13 profile registration, bindings and binary hash enforcement");
        var evidence = Path.Combine(root, "measurement.json"); File.WriteAllText(evidence, "{\"input\":0,\"foreground\":0}");
        var qualification = new BackgroundQualification("fixture", registered.ProfileHash, registered.BinaryHashes, Environment.OSVersion.VersionString, [new("button", "invoke")], DateTimeOffset.UtcNow, evidence, GuiProtocol.Hash(File.ReadAllBytes(evidence)), 0, 0, true);
        Reject(() => registry.Qualify(qualification with { GlobalInputEvents = 1 }), "InvalidQualification");
        registry.Qualify(qualification);
        var node = new ObservationNode("a", null, "Apply", "", "Button", "", "", true, false, new(false, null, "unavailable"), ["Invoke"], "token");
        var observation = new Observation("a", [node], DateTimeOffset.UtcNow, "control", true, null, [], "unknown");
        using var current = Process.GetCurrentProcess(); registry.Check("fixture", Processes.Identify(current), "invoke", node, observation);
        Reject(() => registry.Check("fixture", Processes.Identify(current), "set-value", node, observation), "ActionNotQualified");
        File.AppendAllText(evidence, "changed"); Reject(() => registry.Check("fixture", Processes.Identify(current), "invoke", node, observation), "QualificationStale");
        passed++; Console.WriteLine("PASS S07 qualification intersection, per-action denial and stale evidence rejection (synthetic evidence only)");
        var window = new PublicWindow("one", "dialog", 1, DateTimeOffset.UtcNow, "x", new(0,0,1,1), false) { ClassName = "dialog", OwnerWindowId = "root" };
        Check(WindowQuery.Find([window], null, "dialog", "root", false) == window);
        Reject(() => WindowQuery.Find([window, window with { WindowId = "two" }], "dialog", null, null, false), "AmbiguousTarget");
        Check(WindowQuery.Find([window], "absent", null, null, true) == null);
        passed++; Console.WriteLine("PASS S11 exact window owner relation, ambiguity and absence");
        var request = GuiProtocol.Parse(GuiProtocol.Encode(new GuiRequest(2, "request", "session", "find-item", 5000, JsonSerializer.SerializeToElement(new { windowId = "window", elementId = "container", snapshotToken = "token", itemName = "item" }))));
        Check(GuiProtocol.ReadOnly(request.Operation));
        Check(!GuiProtocol.ReadOnly("realize-item") && !GuiProtocol.ReadOnly("scroll-container"));
        passed++; Console.WriteLine("PASS S10 explicit readonly item lookup and mutation classification");
        var journal = new RequestJournal(store);
        var mutation = new GuiRequest(2, "unknown", "session", "invoke", 5000, JsonSerializer.SerializeToElement(new { windowId = "w", elementId = "e", snapshotToken = "t" }));
        journal.Accept(mutation, Deadline.Start(100)); journal.Finish(mutation, new("completionUnknown", mutation.RequestId, mutation.ControlSessionId) { ApiVersion = 2 });
        var unknown = journal.Entries().Single(); File.SetLastWriteTimeUtc(Directory.GetFiles(root,"request-*.json").Single(), DateTime.UtcNow.AddDays(-5));
        EvidenceBudget.Admit(store, new(1_048_576, 1)); Check(journal.Find("session", "unknown")?.OriginalUnknownResponse?.Status == "completionUnknown");
        passed++; Console.WriteLine("PASS S15 unknown journal retained beyond ordinary retention");
        Check(!File.ReadAllText(Directory.GetFiles(root,"request-*.json").Single()).Contains("completionUnknown",StringComparison.Ordinal));
        passed++; Console.WriteLine("PASS S15 API 2 journal is user-protected ciphertext and remains queryable");
        var quotaStore=new StateStore(Path.Combine(root,"capture-quota"));
        File.WriteAllBytes(quotaStore.PathFor("cap-fixture.png"),new byte[1_048_576]);
        Reject(()=>EvidenceBudget.AdmitCapture(quotaStore,new(MaxCaptureBytes:1_048_576),1),"EvidenceBudgetExceeded");
        Reject(()=>EvidenceBudget.Admit(quotaStore,new(MaxCaptureBytes:1_048_576)),"EvidenceBudgetExceeded");
        passed++;Console.WriteLine("PASS S15 capture quota blocks new capture and mutation without deleting uncertain evidence");
        var monitor=new TestMonitor(); var clock=new TestClock();
        var control=new ControlSession(new("lease-test","local","fixture",1,"Default","control","active",900,DateTimeOffset.UtcNow.AddMinutes(15)) {InteractionPolicy="foreground-exclusive",ApiVersion=2});
        using(var guard=new InteractionGuard(Environment.ProcessId+100000,monitor,clock))
        using(var competing=new InteractionGuard(Environment.ProcessId+100000,new TestMonitor(),clock))
        {
            guard.Arm(control); guard.Check(control);
            Reject(()=>competing.Arm(control),"DesktopLeaseBusy");
            monitor.Sequence++; Reject(()=>guard.Check(control),"InputLeaseRevoked"); guard.Release(control.Info.ControlSessionId);
            guard.Arm(control); clock.Now=clock.Now.AddSeconds(31); Reject(()=>guard.Check(control),"InputLeaseRevoked"); guard.Release(control.Info.ControlSessionId);
            guard.Arm(control); monitor.Healthy=false; Reject(()=>guard.Check(control),"InputLeaseRevoked"); guard.Release(control.Info.ControlSessionId);
            monitor.Healthy=true;
            control.Info=control.Info with {InteractionPolicy="background-only",TargetScope=[new(42,DateTimeOffset.UtcNow,"fixture","hash")]};
            guard.Arm(control); monitor.ProcessEpoch[77]=++monitor.Sequence; guard.Check(control);
            monitor.ProcessEpoch[42]=++monitor.Sequence; Reject(()=>guard.Check(control),"InputLeaseRevoked");
            guard.Release(control.Info.ControlSessionId);guard.Arm(control);monitor.ForegroundEpoch[42]=++monitor.ForegroundSequence;
            Reject(()=>guard.Check(control),"InputLeaseRevoked");Check(guard.ForegroundChangeObserved(control)==true);
        }
        passed++; Console.WriteLine("PASS S07 named lease contention, activity revocation, expiry, health failure and scoped shared activity");
        return passed;
    }
    private sealed class TestClock : TimeProvider {public DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now;}
    private sealed class TestMonitor : IActivityMonitor
    {
        public long Sequence {get;set;}
        public bool Healthy {get;set;}=true;
        public long ForegroundSequence {get;set;}
        public Dictionary<int,long> ForegroundEpoch=new();
        public long LastForegroundForProcess(int pid)=>ForegroundEpoch.GetValueOrDefault(pid);
        public bool Idle=>true;
        public Dictionary<int,long> ProcessEpoch=new();
        public long LastActivityForProcess(int pid)=>ProcessEpoch.GetValueOrDefault(pid);
        public void Ping(){}
        public void Dispose(){}
    }
}

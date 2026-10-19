using System.Diagnostics;
using DesktopTestDriver;

internal sealed class FakeInput : IInputBackend
{
    public readonly List<string> Events = [];
    public bool PhysicalDown;
    public int FailDown;
    public int FailUp;
    public bool IsDown(string kind, int code) => PhysicalDown;
    public void Down(string kind, int code)
    {
        Events.Add($"down:{kind}:{code}");
        if (FailDown > 0 && --FailDown == 0) throw new DriverException(Error.InjectionFailed);
    }
    public void Up(string kind, int code)
    {
        Events.Add($"up:{kind}:{code}");
        if (FailUp > 0 && --FailUp == 0) throw new DriverException(Error.InjectionFailed);
    }
    public void Move(int x, int y) => Events.Add($"move:{x}:{y}");
    public void Wheel(int delta) => Events.Add($"wheel:{delta}");
}
internal sealed class FakeDesktop(WindowIdentity window) : IDesktop
{
    public Observation Observation = new(1, "Default", window.Hwnd, [window], [new("negative-origin", new(-1920, 0, 1920, 1080), 144, 144, false)], false);
    public bool Conflict;
    public bool PhysicalConflict => Conflict;
    public string Pixel = "fixture-pixels";
    public Observation Inspect() => Observation;
    public string Capture(WindowIdentity target, string destination) { File.WriteAllText(destination, Pixel); return Wire.FileHash(destination); }
    public UiNode[] Controls(WindowIdentity target) => [new("fixture", "button", "Button", target.Bounds, true, null),
        new("click", "FixtureClick", "Button", target.Bounds, true, null), new("text", "FixtureText", "Edit", target.Bounds, true, null)];
    public bool Focus(WindowIdentity target) { Observation = Observation with { Foreground = target.Hwnd }; return true; }
}
internal sealed class Fixture : IDisposable
{
    public string Parent { get; } = Path.Combine(Path.GetTempPath(), "DesktopTestDriver-tests-" + Guid.NewGuid().ToString("N"));
    public RunStore Store { get; }
    public FakeInput Backend { get; } = new();
    public FakeDesktop Desktop { get; }
    public InputController Input { get; }
    public CommandEngine Engine { get; }
    public bool WatchdogAlive = true;
    public WindowIdentity Window { get; } = new(123, Environment.ProcessId, DateTimeOffset.UtcNow, Environment.ProcessPath!, new string('a', 64), "Owned fixture", new(-1800, 100, 800, 600));
    private long sequence;
    private readonly string? oldTrust = Environment.GetEnvironmentVariable("DTD_TRUST_STORE");
    private readonly System.Security.Cryptography.ECDsa signer = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
    public Fixture(int frameAge = 1500, int heartbeat = 1000, bool ownedFixture = false, bool applications = false)
    {
        Directory.CreateDirectory(Parent);
        var trust = Path.Combine(Parent, "trust"); RunStore.CreatePrivateDirectory(trust);
        var owner = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        File.WriteAllText(Path.Combine(trust, "owners.json"), Wire.Encode(new[] { new OwnerKey(owner, "test-owner", Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo())) }));
        Environment.SetEnvironmentVariable("DTD_TRUST_STORE", Path.Combine(trust, "owners.json"));
        var runId = "run-" + Guid.NewGuid().ToString("N");
        if (ownedFixture) Window = Window with { ImageHash = Wire.FileHash(Environment.ProcessPath!), Title = "DesktopTestDriver marker " + runId };
        if (applications) Window = Window with { ImageHash = Wire.FileHash(Environment.ProcessPath!) };
        var plan = ownedFixture ? DesktopFixturePlan.Create("host") : ExtensionTests.Plan();
        var config = new RunConfig { RunId = runId, MachineId = "test", Role = "host", TargetSession = 1,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5), MaxSeconds = ownedFixture ? 300 : 60, HeartbeatMilliseconds = heartbeat, MaxHoldMilliseconds = 1500,
            FrameMaxAgeMilliseconds = frameAge, AllowInput = true, Recovery = RecoveryGrade.R1, RecoveryEvidence = "mock-only-never-deploy",
            Windows = [new(Window.ImagePath, Window.ImageHash, Window.Title)], AllowedKeys = [16, 17, 65, 66],
            Fixture = ownedFixture ? new(Window.ImagePath, Window.ImageHash, 300) : null,
            Applications = applications ? [new("idle", Window.ImagePath, Window.ImageHash, ["--idle"])] : null,
            Delegation = new(owner, "fixture-requester", "test-owner", [plan.Id], Authorization.Actions, Authorization.Permissions, new(1, 67_108_864, true), "")
                { ScenarioHashes = new() { [plan.Id] = plan.Hash } } };
        config = Authorization.Sign(config, signer);
        var source = Path.Combine(Parent, "source.json"); File.WriteAllText(source, Wire.Encode(config));
        Store = RunStore.Create(Path.Combine(Parent, "state"), source);
        Desktop = new(Window); Input = new(Store, Backend); Engine = new(Store, Desktop, Input, () => WatchdogAlive);
    }
    public Command Command(string name) => new() { RunId = Store.Config.RunId, MachineId = "test", Role = "host", CommandId = Guid.NewGuid().ToString("N"),
        Sequence = Interlocked.Increment(ref sequence), TargetSession = 1, ExpectedDesktop = "Default", Deadline = DateTimeOffset.UtcNow.AddSeconds(10), ManifestHash = Store.ManifestHash,
        Name = name, ExpectedWindow = Window };
    public async Task<Frame> Snapshot()
    {
        var result = await Engine.Execute(Command("snapshot"));
        Tests.Check(result.ErrorCode == Error.None, "snapshot failed " + result.ErrorCode);
        return (Frame)result.Evidence!;
    }
    public RunConfig ForPlan(ScenarioPlan plan) => Authorization.Sign(Store.Config with { Delegation = Store.Config.Delegation! with
        { Scenarios = [plan.Id], ScenarioHashes = new() { [plan.Id] = plan.Hash } } },signer);
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DTD_TRUST_STORE", oldTrust);
        signer.Dispose();
        // Only this test-owned, freshly generated leaf may be removed recursively.
        if (Path.GetFullPath(Parent).StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(Parent).StartsWith("DesktopTestDriver-tests-", StringComparison.Ordinal))
            Directory.Delete(Parent, true);
    }
}
internal sealed class FakeNode(string id) : IClientNode
{
    public string MachineId => id;
    public int Stops;
    public bool Clean = true;
    public Error Failure = Error.None;
    public Task<Result> Execute(Command command, CancellationToken cancellation) => Task.FromResult(new Result(command.CommandId, true, true, false, Failure, DateTimeOffset.UtcNow, 0, null));
    public Task<bool> Stop(CancellationToken cancellation) { Interlocked.Increment(ref Stops); return Task.FromResult(Clean); }
}
internal sealed class JournalInput(string root) : IInputBackend
{
    public bool IsDown(string kind, int code) => false;
    public void Down(string kind, int code) => File.AppendAllText(Path.Combine(root, "injection.txt"), $"down:{kind}:{code}\n");
    public void Up(string kind, int code) => File.AppendAllText(Path.Combine(root, "recovery.txt"), $"up:{kind}:{code}\n");
    public void Move(int x, int y) => throw new NotSupportedException();
    public void Wheel(int delta) => throw new NotSupportedException();
}

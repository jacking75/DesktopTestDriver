using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DesktopTestDriver;

public enum Verdict { Pass, Fail, Blocked, NotRun }
public sealed record Assertion(string Fact, string Relation, string Expected, string Source, string Role);
public sealed record ScenarioStep(string Id, string Role, string Action, Dictionary<string, string> Arguments, Assertion[] Assertions, int TimeoutMilliseconds = 3000);
public sealed record ScenarioPlan(string Id, string Adapter, string Purpose, string[] Coverage, string[] Preconditions, string[] Evidence,
    string StopCondition, string Cleanup, string Reuse, int TargetSeconds, int MaximumSeconds, int ScreenSeconds, RecoveryGrade Recovery, ScenarioStep[] Steps)
{
    public void Validate()
    {
        if (!Wire.Id(Id) || !Wire.Id(Adapter) || string.IsNullOrWhiteSpace(Purpose) || Coverage.Length == 0 || Preconditions.Length == 0 || Evidence.Length == 0 ||
            string.IsNullOrWhiteSpace(StopCondition) || string.IsNullOrWhiteSpace(Cleanup) || string.IsNullOrWhiteSpace(Reuse) ||
            TargetSeconds is < 1 or > 600 || MaximumSeconds < Math.Max(10,TargetSeconds) || MaximumSeconds > 900 || ScreenSeconds is < 0 or > 300 ||
            !Enum.IsDefined(Recovery) || Steps.Length is < 1 or > 128 || Steps.Select(s => s.Id).Distinct().Count() != Steps.Length)
            throw new DriverException(Error.InvalidRequest);
        foreach (var step in Steps)
        {
            if (!Wire.Id(step.Id) || !Wire.Id(step.Role) || !Wire.Id(step.Action) || step.TimeoutMilliseconds is < 1 or > 5000 || step.Assertions.Length == 0 ||
                step.Assertions.Any(a => !Wire.Id(a.Fact) || a.Relation is not ("eq" or "ge" or "le") || !Wire.Id(a.Source) || !Wire.Id(a.Role)))
                throw new DriverException(Error.InvalidRequest);
        }
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public string Hash => Wire.Hash(Encoding.UTF8.GetBytes(Wire.Encode(this)));
}
public sealed record ScenarioContext(string RunId, string CandidateHash, string ToolHash, string SettingsHash, RunConfig[] Machines, bool Simulation)
{
    public RunConfig Machine(string role) => Machines.SingleOrDefault(m => m.Role == role) ?? throw new DriverException(Error.DelegationMismatch);
    public void Validate(ScenarioPlan plan)
    {
        plan.Validate();
        if (!Wire.Id(RunId) || !RunConfig.IsHash(CandidateHash) || !RunConfig.IsHash(ToolHash) || !RunConfig.IsHash(SettingsHash) ||
            Machines.Length == 0 || Machines.Select(m => m.Role).Distinct().Count() != Machines.Length) throw new DriverException(Error.InvalidRequest);
        foreach (var machine in Machines)
        {
            machine.Validate(); Authorization.Verify(machine, requireCurrentAccount: false);
            if (machine.RunId != RunId || machine.ExpiresAt <= DateTimeOffset.UtcNow || machine.Delegation == null || !machine.Delegation.Scenarios.Contains(plan.Id) ||
                machine.Delegation.ScenarioHashes.GetValueOrDefault(plan.Id) != plan.Hash ||
                machine.MaxSeconds < plan.MaximumSeconds || machine.Recovery < plan.Recovery) throw new DriverException(Error.DelegationMismatch);
        }
        foreach (var step in plan.Steps) _ = Machine(step.Role);
    }
}
public sealed record ObservationRecord(string RunId, string ScenarioId, string StepId, string MachineId, string Role, string Source,
    string CandidateHash, string ToolHash, string SettingsHash, string ArtifactHash, long Tick, DateTimeOffset At, bool Simulated, Dictionary<string, string> Facts);
public sealed record StepReport(string Id, Verdict Verdict, Error Error, ObservationRecord[] Evidence);
public sealed record ScenarioReport(string RunId, string ScenarioId, string PlanHash, string CandidateHash, string ToolHash, string SettingsHash,
    Verdict Verdict, bool Simulated, bool CleanupVerified, double DurationMilliseconds, StepReport[] Steps);
public interface IScenarioAdapter
{
    string Id { get; }
    Task Prepare(ScenarioPlan plan, ScenarioContext context, CancellationToken cancellation);
    Task<ObservationRecord[]> Execute(ScenarioStep step, ScenarioPlan plan, ScenarioContext context, CancellationToken cancellation);
    Task<bool> Cleanup(ScenarioContext context, CancellationToken cancellation);
}
public sealed class AdapterRegistry
{
    private readonly Dictionary<string, IScenarioAdapter> adapters = [];
    public void Register(IScenarioAdapter adapter)
    {
        if (!Wire.Id(adapter.Id) || !adapters.TryAdd(adapter.Id, adapter)) throw new DriverException(Error.InvalidRequest);
    }
    public IScenarioAdapter Resolve(string id) => adapters.TryGetValue(id, out var value) ? value : throw new DriverException(Error.Unsupported);
}
public static class ResultJudge
{
    public static bool Matches(Assertion rule, IEnumerable<ObservationRecord> evidence)
    {
        var candidates = evidence.Where(e => e.Source == rule.Source && e.Role == rule.Role && e.Facts.ContainsKey(rule.Fact)).ToArray();
        if (candidates.Length != 1) return false;
        var actual = candidates[0].Facts[rule.Fact];
        if (rule.Relation == "eq") return actual == rule.Expected;
        return double.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && double.IsFinite(a) &&
            double.TryParse(rule.Expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && double.IsFinite(b) && (rule.Relation == "ge" ? a >= b : a <= b);
    }
    public static void Bind(ObservationRecord record, ScenarioStep step, ScenarioPlan plan, ScenarioContext context)
    {
        var machine = context.Machine(record.Role);
        if (record.RunId != context.RunId || record.ScenarioId != plan.Id || record.StepId != step.Id || record.MachineId != machine.MachineId ||
            record.CandidateHash != context.CandidateHash || record.ToolHash != context.ToolHash || record.SettingsHash != context.SettingsHash ||
            record.Simulated != context.Simulation || !RunConfig.IsHash(record.ArtifactHash) || record.Tick <= 0 || record.At > DateTimeOffset.UtcNow.AddSeconds(1) ||
            DateTimeOffset.UtcNow - record.At > TimeSpan.FromSeconds(10)) throw new DriverException(Error.ObservationUnavailable);
    }
}
public sealed class ScenarioRunner(AdapterRegistry registry)
{
    public async Task<ScenarioReport> Run(ScenarioPlan plan, ScenarioContext context, CancellationToken cancellation = default)
    {
        context.Validate(plan);
        var adapter = registry.Resolve(plan.Adapter);
        var began = Stopwatch.GetTimestamp();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        // Reserve 8 seconds within the same global bound for bounded cleanup and report construction.
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, plan.MaximumSeconds - 8)));
        var reports = new List<StepReport>(); var verdict = Verdict.Pass; bool clean;
        try
        {
            await adapter.Prepare(plan, context, deadline.Token).WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
            foreach (var step in plan.Steps)
            {
                using var stepDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                stepDeadline.CancelAfter(step.TimeoutMilliseconds);
                var records = await adapter.Execute(step, plan, context, stepDeadline.Token).WaitAsync(stepDeadline.Token);
                foreach (var record in records) ResultJudge.Bind(record, step, plan, context);
                var passed = step.Assertions.All(a => ResultJudge.Matches(a, records));
                reports.Add(new(step.Id, passed ? Verdict.Pass : Verdict.Fail, passed ? Error.None : Error.ObservationUnavailable, records));
                if (!passed) { verdict = Verdict.Fail; break; }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            verdict = reports.Count == 0 && e is DriverException { Code: Error.RecoveryRequired or Error.DelegationMismatch or Error.Unsupported } ? Verdict.Blocked : Verdict.Fail;
            reports.Add(new(plan.Steps[Math.Min(reports.Count, plan.Steps.Length - 1)].Id, verdict, e is DriverException d ? d.Code : Error.Expired, []));
        }
        finally
        {
            deadline.Cancel();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(5, Math.Max(.01, plan.MaximumSeconds - Stopwatch.GetElapsedTime(began).TotalSeconds))));
            try { clean = await adapter.Cleanup(context, cleanup.Token).WaitAsync(cleanup.Token); } catch { clean = false; }
        }
        if (!clean) verdict = Verdict.Fail;
        return new(context.RunId, plan.Id, plan.Hash, context.CandidateHash, context.ToolHash, context.SettingsHash, verdict, context.Simulation, clean,
            Stopwatch.GetElapsedTime(began).TotalMilliseconds, reports.ToArray());
    }
}

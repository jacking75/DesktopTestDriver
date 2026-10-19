using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DesktopTestDriver;

// Independent sample adapter. It never opens a window or injects OS input.
public sealed class CounterAdapter : IScenarioAdapter
{
    public string Id => "counter-example";
    private int count;
    public bool Cleaned { get; private set; }
    public Task Prepare(ScenarioPlan plan, ScenarioContext context, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!context.Simulation) throw new DriverException(Error.Unsupported);
        count = 0; Cleaned = false; return Task.CompletedTask;
    }
    public Task<ObservationRecord[]> Execute(ScenarioStep step, ScenarioPlan plan, ScenarioContext context, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (step.Action == "increment") count++;
        else if (step.Action != "observe") throw new DriverException(Error.Unsupported);
        var facts = new Dictionary<string, string> { ["count"] = count.ToString(CultureInfo.InvariantCulture) };
        var record = new ObservationRecord(context.RunId, plan.Id, step.Id, context.Machine(step.Role).MachineId, step.Role, "sample-observer",
            context.CandidateHash, context.ToolHash, context.SettingsHash, Wire.Hash(Encoding.UTF8.GetBytes(Wire.Encode(facts))), Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow, true, facts);
        return Task.FromResult(new[] { record });
    }
    public Task<bool> Cleanup(ScenarioContext context, CancellationToken cancellation) { Cleaned = true; return Task.FromResult(true); }
}

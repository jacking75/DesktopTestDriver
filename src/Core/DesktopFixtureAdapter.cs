using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DesktopTestDriver;

public static class DesktopFixturePlan
{
    public const string Text = "DesktopTestDriver 한글";
    public static ScenarioPlan Create(string role)
    {
        if (!Wire.Id(role)) throw new DriverException(Error.InvalidRequest);
        ScenarioStep Action(string id) => new(id, role, id, [], [new("action-receipt", "eq", "true", "driver-local", role)], 5000);
        return new("desktop-fixture", "desktop-fixture", "독립 fixture 화면·입력·소유 자원 정리", ["D22"],
            ["signed-fixture", "R1-proof", "console-ready", "independent-watchdog"], ["fixture-input", "frames", "audit", "cleanup"],
            "첫 실패 즉시 종료", "모든 노드 stop 및 소유 fixture 해제", "동일 후보 로컬 계약 검사 재사용", 60, 120, 112, RecoveryGrade.R1,
            [Action("launch"), Action("click"), Action("drag"), Action("wheel"), Action("text"), Action("key"),
                new("observe", role, "observe", [], [new("clicks", "ge", "1", "fixture-observer", role),
                    new("drag", "ge", "1", "fixture-observer", role), new("wheel", "ge", "1", "fixture-observer", role),
                    new("key-events", "ge", "2", "fixture-observer", role), new("text-matches", "eq", "true", "fixture-observer", role),
                    new("keys-released", "eq", "true", "fixture-observer", role)], 5000)]);
    }
}

public sealed class DesktopFixtureAdapter(NodeUi ui, RunConfig config, Func<CancellationToken, Task<bool>> ready) : IScenarioAdapter
{
    public string Id => "desktop-fixture";
    private NodeLease? lease;
    private FixtureSample? baseline;
    public async Task Prepare(ScenarioPlan plan, ScenarioContext context, CancellationToken cancellation)
    {
        if (context.Machines.Length != 1 || Wire.Encode(context.Machine(config.Role)) != Wire.Encode(config) || config.Fixture == null ||
            config.Fixture.MaxSeconds < plan.MaximumSeconds || !config.AllowInput || !config.AllowedKeys.Contains(16) || !config.Delegation!.Evidence.Images ||
            plan.Hash != DesktopFixturePlan.Create(config.Role).Hash) throw new DriverException(Error.DelegationMismatch);
        foreach (var action in new[] { "fixture-start", "fixture-observe", "inspect", "snapshot", "focus", "click", "drag", "wheel", "text", "chord", "heartbeat" })
            Authorization.Require(config, action);
        if (!await ready(cancellation)) throw new DriverException(Error.RecoveryRequired);
        lease = ui.BeginLease();
    }
    public async Task<ObservationRecord[]> Execute(ScenarioStep step, ScenarioPlan plan, ScenarioContext context, CancellationToken cancellation)
    {
        if (lease?.Healthy != true || step.Role != config.Role) throw new DriverException(Error.TransportLost);
        var facts = new Dictionary<string, string> { ["action-receipt"] = "true" }; var source = "driver-local";
        var title = "DesktopTestDriver marker " + config.RunId;
        if (step.Action == "launch")
        {
            var launched = await ui.Send("fixture-start", cancellation);
            baseline = Wire.Decode<FixtureObservation>(Wire.Encode(launched.Evidence)).Sample;
            if (baseline.HeldVirtualKeys.Length != 0 || baseline.Events.Length != 0 || baseline.Text.Length != 0 || baseline.Clicks != 0 || baseline.Drag != 0 || baseline.Wheel != 0)
                throw new DriverException(Error.UserInputConflict);
        }
        else if (step.Action == "observe")
        {
            if (baseline == null) throw new DriverException(Error.ObservationUnavailable);
            var began = Stopwatch.GetTimestamp();
            do
            {
                var observed = Wire.Decode<FixtureObservation>(Wire.Encode((await ui.Send("fixture-observe", cancellation)).Evidence));
                facts = Judge(baseline, observed.Sample);
                if (step.Assertions.All(a => facts.TryGetValue(a.Fact, out var v) && (a.Relation == "eq" ? v == a.Expected : int.Parse(v, CultureInfo.InvariantCulture) >= int.Parse(a.Expected, CultureInfo.InvariantCulture)))) break;
                await Task.Delay(20, cancellation);
            } while (Stopwatch.GetElapsedTime(began).TotalSeconds < 2);
            source = "fixture-observer";
        }
        else
        {
            var frame = await ui.Read(title, cancellation);
            if (step.Action == "text")
            {
                await ui.Click(frame, new("FixtureText"), cancellation);
                await ui.Send("text", cancellation, await ui.Read(title, cancellation), text: DesktopFixturePlan.Text);
            }
            else if (step.Action == "key") await ui.Chord(frame, [16], cancellation);
            else
            {
                var button = NodeUi.Select(frame, new("FixtureClick"));
                var x = button.Bounds.X + button.Bounds.Width / 2 - frame.Window.Bounds.X;
                var y = button.Bounds.Y + button.Bounds.Height / 2 - frame.Window.Bounds.Y;
                if (step.Action is not ("click" or "drag" or "wheel")) throw new DriverException(Error.Unsupported);
                await ui.Send(step.Action, cancellation, frame, x, y, endX: x + Math.Min(16, button.Bounds.Width / 4), endY: y, delta: 120);
            }
        }
        return [new(context.RunId, plan.Id, step.Id, config.MachineId, config.Role, source, context.CandidateHash, context.ToolHash,
            context.SettingsHash, Wire.Hash(Encoding.UTF8.GetBytes(Wire.Encode(facts))), Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow, context.Simulation, facts)];
    }
    public static Dictionary<string, string> Judge(FixtureSample before, FixtureSample after)
    {
        if (before.RunId != after.RunId || before.Identity != after.Identity || before.StartTick != after.StartTick || before.Session != after.Session ||
            before.Window != after.Window || after.Tick < before.Tick || after.Events.Length < before.Events.Length ||
            !after.ObservationValid || after.ObservationOverflow || !after.Foreground || after.Utc > DateTimeOffset.UtcNow.AddSeconds(1) ||
            DateTimeOffset.UtcNow - after.Utc > TimeSpan.FromSeconds(2)) throw new DriverException(Error.ObservationUnavailable);
        var events = after.Events.Skip(before.Events.Length).ToArray(); var held = new HashSet<(string, int)>(); var previous = before.Tick;
        foreach (var e in events)
        {
            if (e.Tick < previous || e.Tick > after.Tick || e.Kind is not ("key" or "button")) throw new DriverException(Error.ObservationUnavailable);
            previous = e.Tick;
            if (e.Action == "down") held.Add((e.Kind, e.Code));
            else if (e.Action == "up") held.Remove((e.Kind, e.Code));
            else throw new DriverException(Error.ObservationUnavailable);
        }
        if (after.Clicks < before.Clicks || after.Drag < before.Drag || after.Wheel < before.Wheel) throw new DriverException(Error.ObservationUnavailable);
        return new() { ["clicks"] = (after.Clicks - before.Clicks).ToString(CultureInfo.InvariantCulture),
            ["drag"] = (after.Drag - before.Drag).ToString(CultureInfo.InvariantCulture), ["wheel"] = (after.Wheel - before.Wheel).ToString(CultureInfo.InvariantCulture),
            ["key-events"] = events.Count(e => e.Kind == "key").ToString(CultureInfo.InvariantCulture),
            ["text-matches"] = (after.Text == DesktopFixturePlan.Text && before.Text.Length == 0).ToString().ToLowerInvariant(),
            ["keys-released"] = (held.Count == 0 && after.HeldVirtualKeys.Length == 0).ToString().ToLowerInvariant() };
    }
    public async Task<bool> Cleanup(ScenarioContext context, CancellationToken cancellation)
    {
        try { return await ui.Stop(cancellation); }
        finally { if (lease != null) await lease.DisposeAsync(); }
    }
}

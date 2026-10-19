using System.Diagnostics;

namespace DesktopTestDriver;

public sealed record FixtureStep(string Id, string Operation, string Expected, int TimeoutMilliseconds = 3000);
public sealed record FixtureDefinition(string Id, string Purpose, RecoveryGrade Recovery, string[] Environments, bool ScreenOccupied,
    int MinimumSeconds, FixtureStep[] Steps, FixtureStep[] Restore, int MaximumSeconds = 900)
{
    private static readonly HashSet<string> Operations = ["snapshot", "verify-signature", "install-candidate", "install-previous", "apply-update", "tamper-update", "verify-rollback", "start-service", "inject-service-failure", "trigger-uac", "request-sas", "lock", "verify-tls", "configure-nat", "verify-turn", "start-load", "collect-metrics", "restore-baseline"];
    public void Validate()
    {
        if (!Wire.Id(Id) || string.IsNullOrWhiteSpace(Purpose) || !Enum.IsDefined(Recovery) || Environments.Length == 0 || MinimumSeconds <= 0 || MaximumSeconds < Math.Max(10,MinimumSeconds) || MaximumSeconds > 7800 ||
            ScreenOccupied && (MinimumSeconds > 300 || MaximumSeconds > 900) || Steps.Length == 0 || Restore.Length == 0 || Restore.Last().Operation != "restore-baseline") throw new DriverException(Error.InvalidRequest);
        foreach (var step in Steps.Concat(Restore))
            if (!Wire.Id(step.Id) || !Operations.Contains(step.Operation) || string.IsNullOrWhiteSpace(step.Expected) || step.TimeoutMilliseconds is < 1 or > 5000) throw new DriverException(Error.InvalidRequest);
        if (Steps.Any(s => s.Operation is "trigger-uac" or "request-sas" or "lock" or "apply-update" or "tamper-update") && Recovery != RecoveryGrade.R2)
            throw new DriverException(Error.RecoveryRequired);
        if (Steps.Any(s => s.Operation == "start-load") && (MinimumSeconds < 7200 || ScreenOccupied)) throw new DriverException(Error.InvalidRequest);
    }
}
public sealed record FixtureReceipt(string RunId, string FixtureId, string StepId, bool Verified, bool Simulated, string EvidenceHash);
public interface IFixtureBackend
{
    Task<FixtureReceipt> Execute(FixtureDefinition fixture, FixtureStep step, string runId, CancellationToken cancellation);
}
public sealed record FixtureReport(Verdict Verdict, bool Simulated, bool Restored, FixtureReceipt[] Receipts);
public static class FixtureRunner
{
    // D17 exercises this with mock backends. Real execution requires the equipment-specific backend and live recovery proof.
    public static async Task<FixtureReport> Run(FixtureDefinition fixture, string runId, IFixtureBackend backend, bool simulated,
        string[] environments, Func<CancellationToken, Task<bool>> verifyRecovery, CancellationToken cancellation = default, TimeProvider? timeProvider = null)
    {
        fixture.Validate();
        if (!Wire.Id(runId) || fixture.Environments.Except(environments).Any()) return new(Verdict.Blocked, simulated, false, []);
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(cancellation); preparation.CancelAfter(5000);
        if (!simulated && !await verifyRecovery(preparation.Token).WaitAsync(preparation.Token)) return new(Verdict.Blocked, false, false, []);
        var clock = timeProvider ?? TimeProvider.System;
        var receipts = new List<FixtureReceipt>(); var verdict = Verdict.Pass; var restored = true; var started = clock.GetTimestamp();
        long? loadStarted = null;
        using var global = CancellationTokenSource.CreateLinkedTokenSource(cancellation); global.CancelAfter(TimeSpan.FromSeconds(fixture.MaximumSeconds - 5));
        async Task<bool> Invoke(FixtureStep step, CancellationToken parent)
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(parent); bound.CancelAfter(step.TimeoutMilliseconds);
            var r = await backend.Execute(fixture,step,runId,bound.Token).WaitAsync(bound.Token);
            if (receipts.Count > 0 && receipts[^1].StepId == step.Id) receipts[^1] = r; else receipts.Add(r);
            return r.RunId == runId && r.FixtureId == fixture.Id && r.StepId == step.Id && r.Simulated == simulated && r.Verified && RunConfig.IsHash(r.EvidenceHash);
        }
        try
        {
            foreach (var step in fixture.Steps)
            {
                // Keep each equipment call bounded while measuring the full load interval monotonically.
                // A quick start-load/metrics receipt must never stand in for a two-hour observation.
                if (!simulated && loadStarted is { } loadTick && step.Operation == "collect-metrics")
                {
                    while (clock.GetElapsedTime(loadTick).TotalSeconds < fixture.MinimumSeconds)
                    {
                        if (!await Invoke(step, global.Token)) throw new DriverException(Error.ObservationUnavailable);
                        var remaining = fixture.MinimumSeconds - clock.GetElapsedTime(loadTick).TotalSeconds;
                        if (remaining > 0) await Task.Delay(TimeSpan.FromSeconds(Math.Min(1, remaining)), clock, global.Token);
                    }
                }
                if (!await Invoke(step,global.Token)) { verdict = Verdict.Fail; break; }
                if (step.Operation == "start-load") loadStarted = clock.GetTimestamp();
            }
            if (!simulated && clock.GetElapsedTime(loadStarted ?? started).TotalSeconds < fixture.MinimumSeconds) verdict = Verdict.Fail;
        }
        catch { verdict = Verdict.Fail; }
        finally
        {
            using var cleanup = new CancellationTokenSource(5000);
            // Keep restoration attempts independent of a failed/cancelled feature step.
            foreach (var step in fixture.Restore)
                try { restored &= await Invoke(step,cleanup.Token); } catch { restored = false; }
        }
        return new(restored ? verdict : Verdict.Fail, simulated, restored, receipts.ToArray());
    }
}
public sealed class SimulatedFixtureBackend : IFixtureBackend
{
    public string? FailStep { get; init; }
    public Task<FixtureReceipt> Execute(FixtureDefinition fixture, FixtureStep step, string runId, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult(new FixtureReceipt(runId,fixture.Id,step.Id,step.Id != FailStep,true,
            Wire.Hash(System.Text.Encoding.UTF8.GetBytes(Wire.Encode(new { runId, fixtureId = fixture.Id, stepId = step.Id, step.Operation })))));
    }
}

using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DesktopTestDriver;

public sealed record ScriptStepReport(string Id, Verdict Verdict, int Attempts = 0, Error Error = Error.None, string? Detail = null);
public sealed record ScriptCleanup(string Role, string RunId, bool Verified, ControllerReply? Reply);
public sealed record ScriptReport(string ExecutionId, string ScenarioId, string PlanHash, Verdict Verdict,
    double DurationMilliseconds, string EvidenceDirectory, ScriptStepReport[] Steps, ScriptCleanup[] Cleanup, Error Error);

public static class ScriptScenarioRunner
{
    private sealed class Journal(string directory)
    {
        private long bytes;
        public void Append(object value)
        {
            var line = Wire.Encode(value) + "\n";
            bytes += Encoding.UTF8.GetByteCount(line);
            if (bytes > 16_777_216) throw new DriverException(Error.ObservationUnavailable);
            File.AppendAllText(Path.Combine(directory, "events.jsonl"), line, new UTF8Encoding(false));
        }
    }
    public static async Task<ScriptReport> Run(string stateRoot, ScriptExecution execution,
        Func<ControllerRequest, CancellationToken, Task<ControllerReply>> control, CancellationToken cancellation)
    {
        execution.Validate();
        var plan = execution.Plan;
        foreach (var config in execution.Runs.Values)
            if (config.ExpiresAt < DateTimeOffset.UtcNow.AddSeconds(plan.MaximumSeconds)) throw new DriverException(Error.Expired);
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(stateRoot, "scenarios", id);
        RunStore.CreatePrivateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "plan.json"), Wire.Encode(plan));
        File.WriteAllText(Path.Combine(directory, "execution.json"), Wire.Encode(execution));
        var journal = new Journal(directory);
        var began = Stopwatch.GetTimestamp();
        using var work = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        work.CancelAfter(TimeSpan.FromSeconds(plan.MaximumSeconds - plan.CleanupSeconds));
        var completed = new Dictionary<string, object>();
        var reports = plan.Steps.Select(s => new ScriptStepReport(s.Id, Verdict.NotRun)).ToArray();
        var attempted = new List<string>();
        var cleanup = new List<ScriptCleanup>();
        var error = Error.None;
        var index = -1;
        int commands = 0;
        try
        {
            foreach (var role in plan.Roles)
            {
                work.Token.ThrowIfCancellationRequested();
                var config = execution.Runs[role];
                attempted.Add(role); // A lost begin reply can still mean a running worker; always request cleanup.
                var reply = await control(new("begin", config.MachineId, JsonSerializer.SerializeToElement(config, Wire.Json)), work.Token);
                journal.Append(new { kind = "begin", role, config.RunId, reply });
                Require(reply);
            }
            for (index = 0; index < plan.Steps.Length; index++)
            {
                work.Token.ThrowIfCancellationRequested();
                var template = plan.Steps[index];
                var context = JsonSerializer.SerializeToElement(new { parameters = execution.Parameters, steps = completed }, Wire.Json);
                var step = Wire.Decode<ScriptStep>(ScriptValues.Resolve(JsonSerializer.SerializeToElement(template, Wire.Json), context).GetRawText());
                ScriptCommands.Validate(step.Command, resolved: true);
                foreach (var condition in (step.Expect ?? []).Concat(step.Select?.Where ?? [])) ScriptValues.Validate(condition);
                using var stepLimit = CancellationTokenSource.CreateLinkedTokenSource(work.Token);
                stepLimit.CancelAfter(step.TimeoutMilliseconds);
                int attempts = 0;
                while (true)
                {
                    stepLimit.Token.ThrowIfCancellationRequested();
                    if (++commands > 1024) throw new DriverException(Error.Expired);
                    attempts++;
                    reports[index] = new(step.Id, Verdict.Fail, attempts, Error.ObservationUnavailable);
                    var reply = await control(new("request", execution.Runs[step.Role].RunId, step.Command), stepLimit.Token);
                    Require(reply);
                    var response = JsonSerializer.SerializeToElement(reply.Data, Wire.Json);
                    var receipt = Wire.Decode<Result>(response.GetRawText());
                    if (!receipt.Accepted || !receipt.Executed || receipt.ErrorCode != Error.None)
                    {
                        journal.Append(new { kind = "command-error", step.Id, step.Role, attempts, response });
                        throw new DriverException(receipt.ErrorCode == Error.None ? Error.ObservationUnavailable : receipt.ErrorCode);
                    }
                    var value = response.GetProperty("evidence");
                    bool found = true;
                    if (step.Select is { } select)
                    {
                        if (!ScriptValues.TryPointer(response, select.Path, out var array) || array.ValueKind != JsonValueKind.Array)
                            throw new DriverException(Error.ObservationUnavailable);
                        var candidates = array.EnumerateArray().Where(item => select.Where.All(c => ScriptValues.Matches(item, c))).Take(2).ToArray();
                        if (candidates.Length > 1)
                        {
                            journal.Append(new { kind = "ambiguous-selection", step.Id, step.Role, attempts, response });
                            throw new DriverException(Error.WindowChanged);
                        }
                        found = candidates.Length == 1;
                        value = found ? candidates[0] : JsonSerializer.SerializeToElement<object?>(null, Wire.Json);
                    }
                    var unmet = found ? (step.Expect ?? []).Where(c => !ScriptValues.Matches(value, c)).Select(c => c.Path).ToArray() : [];
                    bool passed = found && unmet.Length == 0;
                    journal.Append(new { kind = "observation", step.Id, step.Role, attempts, response, value, found, passed, unmet });
                    if (passed)
                    {
                        completed.Add(step.Id, new { response, value });
                        reports[index] = new(step.Id, Verdict.Pass, attempts);
                        break;
                    }
                    reports[index] = new(step.Id, Verdict.Fail, attempts, Error.ObservationUnavailable, found ? "expect:" + string.Join(',', unmet) : "selection-empty");
                    if (!step.WaitFor) throw new DriverException(Error.ObservationUnavailable);
                    await Task.Delay(step.PollMilliseconds, stepLimit.Token);
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            error = e is DriverException d ? d.Code : e is OperationCanceledException ? Error.Expired : Error.ObservationUnavailable;
            if (index >= 0 && index < reports.Length)
                reports[index] = reports[index] with { Verdict = Verdict.Fail, Error = error };
        }
        finally
        {
            work.Cancel();
            foreach (var role in attempted.AsEnumerable().Reverse())
            {
                var config = execution.Runs[role];
                ControllerReply? reply = null;
                bool verified = false;
                using var end = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(8,
                    Math.Max(.001, plan.MaximumSeconds - Stopwatch.GetElapsedTime(began).TotalSeconds - 1))));
                try
                {
                    // Deliberately independent of the failed/cancelled work token.
                    reply = await control(new("end", config.MachineId), end.Token);
                    if (reply.Ok && reply.Data != null)
                    {
                        var receipt = Wire.Decode<AgentCleanup>(Wire.Encode(reply.Data));
                        verified = receipt.RunId == config.RunId && receipt.Clean && receipt.NormalDesktop;
                    }
                }
                catch (Exception e) when (e is not OutOfMemoryException) { }
                cleanup.Add(new(role, config.RunId, verified, reply));
            }
        }
        if (cleanup.Count != attempted.Count || cleanup.Any(c => !c.Verified)) error = Error.CleanupIncomplete;
        else if (Stopwatch.GetElapsedTime(began).TotalSeconds > plan.MaximumSeconds) error = Error.Expired;
        var report = new ScriptReport(id, plan.Id, plan.Hash(), error == Error.None ? Verdict.Pass : Verdict.Fail,
            Stopwatch.GetElapsedTime(began).TotalMilliseconds, directory, reports, cleanup.ToArray(), error);
        File.WriteAllText(Path.Combine(directory, "report.json"), Wire.Encode(report));
        return report;
    }
    private static void Require(ControllerReply reply)
    {
        if (!reply.Ok) throw new DriverException(reply.Error == Error.None ? Error.TransportLost : reply.Error);
    }
}

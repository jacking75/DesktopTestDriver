using System.Text.Json;
using DesktopTestDriver;

internal static class ScriptScenarioTests
{
    private static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value, Wire.Json);
    private static ScriptScenario Plan() => Wire.Decode<ScriptScenario>("""
        {
          "schemaVersion":1,"id":"external-app","purpose":"Observe a uniquely identified window and its UIA result",
          "roles":["host"],"parameters":["pid"],"maximumSeconds":30,
          "steps":[
            {"id":"window","role":"host","command":{"name":"inspect"},"waitFor":true,
             "select":{"path":"/evidence/frames","where":[{"path":"/window/pid","op":"eq","value":{"$ref":"/parameters/pid"}}]}},
            {"id":"controls","role":"host","command":{"name":"controls","expectedWindow":{"$ref":"/steps/window/value/window"}},
             "expect":[{"path":"/controls","op":"count-eq","value":1}]},
            {"id":"after","role":"host","command":{"name":"inspect"}}
          ]
        }
        """);
    private static ScriptExecution Execution(Fixture fixture) => new(Plan(), new() { ["pid"] = Json(fixture.Window.Pid) }, new() { ["host"] = fixture.Store.Config });

    public static Task Contract()
    {
        var plan = Plan(); plan.Validate();
        var roundTrip = Wire.Decode<ScriptScenario>(Wire.Encode(plan));
        Tests.Check(roundTrip.Hash() == plan.Hash(), "canonical plan hash survives transport and excludes derived fields");
        Tests.Reject(() => (plan with { SchemaVersion = 2 }).Validate(), Error.InvalidRequest);
        var future = plan.Steps[0] with { Select = new("/evidence/frames", [new("/window/pid", "eq", Json(new Dictionary<string, string> { ["$ref"] = "/steps/controls/value/pid" }))]) };
        Tests.Reject(() => (plan with { Steps = [future, .. plan.Steps[1..]] }).Validate(), Error.InvalidRequest);
        var repeatedInput = plan.Steps[0] with { Command = Json(new { name = "type-window", hwnd = 1, text = "a" }) };
        Tests.Reject(() => (plan with { Steps = [repeatedInput, .. plan.Steps[1..]] }).Validate(), Error.InvalidRequest);
        Tests.Reject(() => ScriptCommands.Validate(Json(new { name = "click-window", hwnd = 1, x = 2 })), Error.InvalidRequest);
        Tests.Reject(() => ScriptCommands.Validate(Json(new { name = "inspect", sequence = 3 })), Error.InvalidRequest);
        Tests.Reject(() => ScriptValues.Tokens("/bad~2escape"), Error.InvalidRequest);
        var resolved = ScriptValues.Resolve(Json(new { data = new Dictionary<string, string> { ["$ref"] = "/parameters/object" } }),
            Json(new { parameters = new { @object = new { hwnd = 42L, items = new[] { true, false } } } }));
        Tests.Check(resolved.GetProperty("data").GetProperty("hwnd").GetInt64() == 42 && resolved.GetProperty("data").GetProperty("items")[0].GetBoolean(), "references preserve object/array/numeric/bool types");
        Tests.Check(ScriptValues.Matches(Json(new { present = (object?)null }), new("/present", "exists", Json(true))), "null is present");
        Tests.Check(!ScriptValues.Matches(Json(new { }), new("/missing", "ne", Json(1))), "missing paths cannot pass ne");
        using var fixture = new Fixture();
        var execution = Execution(fixture); execution.Validate();
        Tests.Reject(() => (execution with { Parameters = new() { ["wrong"] = Json(1) } }).Validate(), Error.InvalidRequest);
        return Task.CompletedTask;
    }

    public static async Task Lifecycle()
    {
        using var fixture = new Fixture();
        var execution = Execution(fixture);
        int inspected = 0, queried = 0, ended = 0;
        Task<ControllerReply> Control(ControllerRequest request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (request.Operation == "begin") return Task.FromResult(new ControllerReply(true));
            if (request.Operation == "end") { ended++; return Task.FromResult(new ControllerReply(true, new AgentCleanup(true, true, fixture.Store.Config.RunId))); }
            var command = request.Arguments!.Value;
            object evidence;
            if (command.GetProperty("name").GetString() == "inspect")
            {
                inspected++;
                evidence = new { frames = inspected == 1 ? Array.Empty<object>() : new object[] { new { window = fixture.Window } } };
            }
            else
            {
                queried++;
                Tests.Check(command.GetProperty("expectedWindow").GetProperty("hwnd").GetInt64() == fixture.Window.Hwnd, "observed window object reaches next command unchanged");
                evidence = new { controls = new[] { new { name = "result" } } };
            }
            return Task.FromResult(new ControllerReply(true, new Result("id", true, true, true, Error.None, DateTimeOffset.UtcNow, 0, evidence)));
        }
        var report = await ScriptScenarioRunner.Run(fixture.Parent, execution, Control, default);
        Tests.Check(report.Verdict == Verdict.Pass && report.Steps[0].Attempts == 2 && queried == 1 && ended == 1, "only unsatisfied read is polled and cleanup occurs once");
        Tests.Check(File.Exists(Path.Combine(report.EvidenceDirectory, "report.json")) && File.ReadAllLines(Path.Combine(report.EvidenceDirectory, "events.jsonl")).Length == 5,
            "controller persists begin and every observed attempt");
    }

    public static async Task FailureCleanup()
    {
        using var fixture = new Fixture();
        var execution = Execution(fixture);
        foreach (var mode in new[] { "ambiguous", "command-error", "assertion", "cancel", "cleanup" })
        {
            int requests = 0, ends = 0;
            using var cancel = new CancellationTokenSource();
            Task<ControllerReply> Control(ControllerRequest request, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested();
                if (request.Operation == "begin") { if (mode == "cancel") cancel.Cancel(); return Task.FromResult(new ControllerReply(true)); }
                if (request.Operation == "end")
                {
                    ends++;
                    return Task.FromResult(new ControllerReply(true, new AgentCleanup(mode != "cleanup", true, fixture.Store.Config.RunId)));
                }
                requests++;
                object evidence = request.Arguments!.Value.GetProperty("name").GetString() == "inspect"
                    ? new { frames = Enumerable.Range(0, mode == "ambiguous" ? 2 : 1).Select(_ => new { window = fixture.Window }).ToArray() }
                    : new { controls = mode == "assertion" ? Array.Empty<string>() : new[] { "result" } };
                return Task.FromResult(new ControllerReply(true, new Result("id", true, true, false,
                    mode == "command-error" ? Error.WindowChanged : Error.None, DateTimeOffset.UtcNow, 0, evidence)));
            }
            var report = await ScriptScenarioRunner.Run(fixture.Parent, execution, Control, cancel.Token);
            Tests.Check(report.Verdict == Verdict.Fail && ends == 1, "failure/cancellation/unclean receipt cannot pass; cleanup gets an independent token: " + mode);
            if (mode is "ambiguous" or "command-error") Tests.Check(requests == 1 && report.Steps[1].Verdict == Verdict.NotRun, "ambiguous target or command failure is never retried");
            if (mode == "assertion") Tests.Check(requests == 2 && report.Steps[2].Verdict == Verdict.NotRun, "first failed expectation skips subsequent commands");
            if (mode == "cancel") Tests.Check(requests == 0, "cancelled preparation cannot inject any command");
            if (mode == "cleanup") Tests.Check(report.Error == Error.CleanupIncomplete, "cleanup receipt overrides passing steps");
        }
    }
}

using System.Diagnostics;
using System.Text.Json;

namespace DesktopTestDriver;

public sealed record ControllerConfiguration(string Host, int Port, string StateRoot);

public static class ApplicationStartup
{
    public static string ControllerRoot(bool serving)
    {
        var config = Wire.Decode<ControllerConfiguration>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Controller.json")));
        if (Uri.CheckHostName(config.Host) == UriHostNameType.Unknown || config.Port is < 25001 or > 25199 ||
            string.IsNullOrWhiteSpace(config.StateRoot)) throw new DriverException(Error.InvalidRequest);
        var root = Path.GetFullPath(config.StateRoot, AppContext.BaseDirectory);
        if (!Directory.Exists(root))
        {
            if (!serving) throw new DriverException(Error.TransportLost);
            ControllerFiles.Initialize(root, config.Host, config.Port);
        }
        var settings = ControllerFiles.Settings(root);
        if (settings.Host != config.Host || settings.Port != config.Port)
        {
            if (!serving) throw new DriverException(Error.InvalidRequest);
            // Never replace routing while another controller owns this state directory.
            using var guard = new FileStream(Path.Combine(root, "agent.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            File.WriteAllText(Path.Combine(root, "controller.json"), Wire.Encode(settings with { Host = config.Host, Port = config.Port }));
        }
        return root;
    }

    public static AgentConfiguration Agent(string path)
    {
        var config = Wire.Decode<AgentConfiguration>(File.ReadAllText(path));
        if (string.IsNullOrWhiteSpace(config.StateRoot)) throw new DriverException(Error.InvalidRequest);
        return config with
        {
            StateRoot = Path.GetFullPath(config.StateRoot, AppContext.BaseDirectory),
            TargetSession = config.TargetSession == 0 ? Process.GetCurrentProcess().SessionId : config.TargetSession
        };
    }

    public static async Task<int> ControllerCommand(string[] args)
    {
        if (args.Length is 2 or 3 && args[0] == "scenario-check")
        {
            var plan = args.Length == 3 ? ScriptExecution.Load(args[1], args[2]).Plan : Wire.Decode<ScriptScenario>(File.ReadAllText(args[1]));
            plan.Validate();
            Console.WriteLine(Wire.Encode(new { valid = true, plan.Id, schemaVersion = plan.SchemaVersion, planHash = plan.Hash(), bindingsChecked = args.Length == 3 }));
            return 0;
        }
        var root = ControllerRoot(args.Length == 0);
        if (args.Length == 0)
        {
            using var stopping = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };
            Console.WriteLine("Controller listening. Ctrl+C to stop.");
            await new ControllerAgent(root).Serve(stopping.Token);
            return 0;
        }
        if (args is ["scenario-run", var scenarioFile, var bindingsFile])
        {
            var execution = ScriptExecution.Load(scenarioFile, bindingsFile);
            using var cancelled = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancelled.Cancel(); };
            Console.CancelKeyPress += handler;
            try
            {
                var result = await ControllerAgent.Call(root, new("scenario", Arguments: JsonSerializer.SerializeToElement(execution, Wire.Json)), cancelled.Token);
                Console.WriteLine(Wire.Encode(result));
                return ControllerCli.Successful(result) ? 0 : 1;
            }
            finally { Console.CancelKeyPress -= handler; }
        }
        ControllerRequest request = args switch
        {
            ["list"] => new("clients"),
            ["shutdown"] => new("shutdown"),
            ["end", var machine] => new("end", machine),
            ["exit", var machine] => new("shutdown-client", machine),
            ["result", var run] => new("result", run),
            ["begin", var machine, var file] => new("begin", machine, JsonSerializer.SerializeToElement(Wire.Decode<RunConfig>(File.ReadAllText(file)), Wire.Json)),
            ["request", var machine, var file] => new("request", machine, Wire.Decode<JsonElement>(File.ReadAllText(file))),
            _ => throw new DriverException(Error.InvalidRequest)
        };
        var reply = await ControllerAgent.Call(root, request, default);
        Console.WriteLine(Wire.Encode(reply));
        return ControllerCli.Successful(reply) ? 0 : 1;
    }
}

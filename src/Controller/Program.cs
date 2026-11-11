using System.IO;
using System.Globalization;
using System.Text;
using DesktopTestDriver;
Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
try
{
    var list = args.ToList();
    if (list is ["--help"] || list is ["-h"]) { Console.WriteLine(ControllerCli.Help); return 0; }
    if (list is ["--operations"]) { Console.WriteLine(OperationHelp.Reference(Operations.All.Select(o => o.Operation))); return 0; }
    if (list is ["--operations", var operationName]) { if (!Operations.Contains(operationName)) throw new GuiException("UnsupportedOperation", "Unknown operation " + operationName); Console.WriteLine(OperationHelp.For(operationName)); return 0; }
    if (list is ["--export-schemas", var schemaDirectory])
    {
        Directory.CreateDirectory(schemaDirectory);
        foreach (var schema in ContractSchemas.Export()) File.WriteAllText(Path.Combine(schemaDirectory, schema.Key), schema.Value.ToJsonString(new() { WriteIndented = true }) + "\n");
        return 0;
    }
    var settings = HostSettings.Controller(list);
    var a = list.ToArray();
    if (a is ["--serve"] || a.Length == 0)
    {
        using var stop = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        await new ControllerHost(settings).Run(stop.Token); return 0;
    }
    if (a is ["--mcp"]) return await McpServer.Run(settings);
    if (a is ["--json-lines"]) return await ControllerCli.JsonLines(settings);
    var command = ControllerCli.Parse(a);
    var reply = await ControllerHost.Call(settings, command);
    Console.WriteLine(GuiProtocol.Encode(reply)); return reply.ExitCode;
}
catch (Exception e)
{
    var reply = GuiResponse.Reject(e is GuiException g ? g.Code : "InvalidCommand", message: e.Message);
    Console.WriteLine(GuiProtocol.Encode(reply)); return reply.ExitCode;
}

internal static class ControllerCli
{
    public static string Help => $"""
        DesktopTestDriver Controller {typeof(ControllerHost).Assembly.GetName().Version?.ToString(3)} (wire {GuiProtocol.WireVersion})
        Trusted networks and PCs only. The connection is not encrypted or authenticated.

        Host
          --serve [--listen ADDR:PORT] [--state DIR]   default 127.0.0.1:{HostSettings.DefaultPort}, %LOCALAPPDATA%\DesktopTestDriver\Controller
          --mcp                                         stdio MCP server (starts a host in-process when none is running)
          --json-lines                                  one command or request per stdin line, one response per stdout line
          --shutdown
        Sessions
          --session-open [--machine M] [--input | --observe] [--launch EXE [--arg A]...]
                         [--target-pid N]... [--target-exe PATH]... [--target-window-title T]
                         [--target-window-title-contains T] [--target-window-class C] [--target-any] [--idle-seconds 900]
          --session-status [ID] [--all]      --session-close ID      --session-pause ID
          --session-resume ID [--expected-revision N]
          --session-reconcile ID --request-id R --resolution effect-confirmed|no-effect-confirmed|abandoned [--evidence-ref E] [--resume]
        Requests
          --request FILE | --request-stdin    --request-status SESSION REQUEST
          --inspect --session ID --window W   --trace-export SESSION OUTPUT_DIR
        Diagnostics
          --status [--machine M]   --doctor [--machine M]   --export-schemas DIR
          --operations [OPERATION]   arguments of every operation (the same reference the MCP tools carry)
        Common options: --listen ADDR:PORT --state DIR --config FILE
        Usage guide: {Path.Combine(AppContext.BaseDirectory, "docs", "agent-usage.md")}
        """;
    public static LocalCommand Parse(string[] a)
    {
        string? Option(string name) { var i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
        IEnumerable<string> Options(string name) { for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) yield return a[i + 1]; }
        switch (a)
        {
            case ["--status"]: return new("status");
            case ["--status", "--machine", var m]: return new("status", MachineId: m);
            case ["--doctor"]: return new("doctor");
            case ["--doctor", "--machine", var m]: return new("doctor", MachineId: m);
            case ["--shutdown"]: return new("shutdown");
            case ["--session-status"]: return new("session-status");
            case ["--session-status", "--all"]: return new("session-status") { All = true };
            case ["--session-status", var sid]: return new("session-status", SessionId: sid);
            case ["--session-close", var sid]: return new("session-close", SessionId: sid);
            case ["--session-pause", var sid]: return new("session-pause", SessionId: sid);
            case ["--session-resume", var sid]: return new("session-resume", SessionId: sid);
            case ["--session-resume", var sid, "--expected-revision", var rev]: return new("session-resume", SessionId: sid) { ExpectedRevision = long.Parse(rev, CultureInfo.InvariantCulture) };
            case ["--request-status", var sid, var rid]: return new("request-status", SessionId: sid, RequestId: rid);
            case ["--trace-export", var sid, var output]: return new("trace-export", SessionId: sid) { Output = Path.GetFullPath(output) };
            case ["--inspect", "--session", var sid, "--window", var window]:
                return new("request", Request: GuiProtocol.Encode(new GuiRequest("inspect-" + Guid.NewGuid().ToString("N")[..12], sid, "inspect", 5000, System.Text.Json.JsonSerializer.SerializeToElement(new { windowId = window }))));
            case ["--request", var file]:
                if (new FileInfo(file).Length > GuiProtocol.MaxRequestBytes) throw new GuiException("RequestTooLarge");
                var json = File.ReadAllText(file, new UTF8Encoding(false, true)); GuiProtocol.Parse(json); return new("request", Request: json);
            case ["--request-stdin"]:
                var stdin = Console.In.ReadToEnd().TrimStart('﻿'); GuiProtocol.Parse(stdin); return new("request", Request: stdin);
        }
        if (a.Length >= 2 && a[0] == "--session-reconcile")
            return new("session-reconcile", SessionId: a[1], RequestId: Option("--request-id") ?? throw new GuiException("InvalidCommand", "--request-id required"))
            { Resolution = Option("--resolution") ?? throw new GuiException("InvalidCommand", "--resolution required"), EvidenceRef = Option("--evidence-ref"), Resume = a.Contains("--resume"), ExpectedRevision = Option("--expected-revision") is { } r ? long.Parse(r, CultureInfo.InvariantCulture) : null };
        if (a.Length >= 1 && a[0] == "--session-open")
        {
            var known = new[] { "--machine", "--launch", "--arg", "--target-pid", "--target-exe", "--target-window-title", "--target-window-title-contains", "--target-window-class", "--idle-seconds" };
            for (int i = 1; i < a.Length; i++)
            {
                if (a[i] is "--input" or "--observe" or "--target-any") continue;
                if (!known.Contains(a[i]) || i + 1 >= a.Length) throw new GuiException("InvalidCommand", "Unknown option: " + a[i]);
                i++;
            }
            if (a.Contains("--input") && a.Contains("--observe")) throw new GuiException("InvalidSessionOptions", "--input and --observe exclude each other");
            var targets = new TargetSpec
            {
                Pids = Options("--target-pid").Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray() is { Length: > 0 } pids ? pids : null,
                Executables = Options("--target-exe").Select(Path.GetFullPath).ToArray() is { Length: > 0 } exes ? exes : null,
                WindowTitle = Option("--target-window-title"), WindowTitleContains = Option("--target-window-title-contains"), WindowClass = Option("--target-window-class"), Any = a.Contains("--target-any")
            };
            return new("session-open", MachineId: Option("--machine"))
            {
                Mode = a.Contains("--observe") ? "observe" : "control", Input = a.Contains("--input"), IdleSeconds = Option("--idle-seconds") is { } idle ? int.Parse(idle, CultureInfo.InvariantCulture) : 900,
                Targets = targets.Empty ? null : targets, Launch = Option("--launch"), LaunchArguments = Options("--arg").ToArray()
            };
        }
        throw new GuiException("InvalidCommand", "Unknown command. See --help");
    }
    /// <summary>Each line is a LocalCommand, or a bare request object (it has an "operation" field).</summary>
    public static async Task<int> JsonLines(ControllerSettings settings)
    {
        string? line;
        while ((line = Console.In.ReadLine()) != null)
        {
            line = line.TrimStart('﻿');
            if (line.Length == 0) continue;
            GuiResponse response;
            try
            {
                if (line.Length > GuiProtocol.MaxRequestBytes * 2) throw new GuiException("RequestTooLarge");
                using var document = System.Text.Json.JsonDocument.Parse(line);
                var command = document.RootElement.TryGetProperty("operation", out _) ? new LocalCommand("request", Request: line) : GuiProtocol.Decode<LocalCommand>(line);
                if (command.Command == "request") GuiProtocol.Parse(command.Request ?? throw new GuiException("InvalidRequest"));
                response = await ControllerHost.Call(settings, command);
            }
            catch (Exception error) { response = GuiResponse.Reject(error is GuiException problem ? problem.Code : "InvalidRequest", message: error.Message); }
            Console.WriteLine(GuiProtocol.Encode(response)); Console.Out.Flush();
        }
        return 0;
    }
}

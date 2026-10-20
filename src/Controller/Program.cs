using System.IO;
using System.Text;
using DesktopTestDriver;
Console.InputEncoding = new UTF8Encoding(false, true); Console.OutputEncoding = new UTF8Encoding(false);
try
{
    var list = args.ToList(); var config = Path.Combine(AppContext.BaseDirectory, "Controller.json");
    if (list.Count >= 2 && list[0] == "--config") { config = Path.GetFullPath(list[1]); list.RemoveRange(0, 2); }
    var a = list.ToArray();
    if (a is ["--help"]) { Console.WriteLine($"DesktopTestDriver 0.3 / GUI API 1\n--status [--machine TARGET]\n--session-open --machine TARGET [--mode control|observe] [--idle-seconds 900]\n--session-status [CONTROL_ID]\n--request FILE\n--request-status CONTROL_ID REQUEST_ID\n--session-close CONTROL_ID\n--config FILE (first option)\nSetup: --initialize STATE ADDRESS PORT | --register MACHINE CERTIFICATE.cer\nHost: --serve | --shutdown\nUsage: {Path.Combine(AppContext.BaseDirectory, "docs", "agent-usage.md")}\nSetup: {Path.Combine(AppContext.BaseDirectory, "scripts", "manage.ps1")}"); return 0; }
    if (a is ["--initialize", var state, var address, var port])
    {
        if (File.Exists(config)) throw new GuiException("ConfigurationExists"); var settings = new ControllerSettings(Path.GetFullPath(state), address, int.Parse(port)); var store = new StateStore(settings.StateDirectory); ConnectionSettings.Initialize(store, "Controller");
        File.WriteAllText(config, GuiProtocol.Encode(settings)); Console.WriteLine(GuiProtocol.Encode(new GuiResponse("completed", Data: new { configuration = config, publicCertificate = store.PathFor("identity.cer") }))); return 0;
    }
    var c = ConnectionSettings.Controller(config);
    if (a is ["--register", var machine, var cert]) { ConnectionSettings.Register(new(c.StateDirectory), machine, cert); Console.WriteLine(GuiProtocol.Encode(new GuiResponse("completed"))); return 0; }
    if (a is ["--serve"] || a.Length == 0) { using var stop = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); }; await new ControllerHost(c).Run(stop.Token); return 0; }
    LocalCommand command;
    if (a is ["--status"]) command = new("status");
    else if (a is ["--status", "--machine", var target]) command = new("status", MachineId: target);
    else if (a is ["--session-status"]) command = new("session-status");
    else if (a is ["--session-status", var sid]) command = new("session-status", SessionId: sid);
    else if (a is ["--session-close", var close]) command = new("session-close", SessionId: close);
    else if (a is ["--request-status", var session, var request]) command = new("request-status", SessionId: session, RequestId: request);
    else if (a is ["--request", var file])
    {
        if (new FileInfo(file).Length > GuiProtocol.MaxRequestBytes) throw new GuiException("RequestTooLarge"); var json = File.ReadAllText(file, new UTF8Encoding(false, true)); _ = GuiProtocol.Parse(json); command = new("request", Request: json);
    }
    else if (a is ["--shutdown"]) command = new("shutdown");
    else if (a.Length >= 3 && a[0] == "--session-open")
    {
        string? targetMachine = null; var mode = "control"; int idle = 900; var seen = new HashSet<string>();
        for (int i = 1; i < a.Length; i += 2) { if (i + 1 >= a.Length || !seen.Add(a[i])) throw new GuiException("InvalidCommand"); switch (a[i]) { case "--machine": targetMachine = a[i + 1]; break; case "--mode": mode = a[i + 1]; break; case "--idle-seconds": idle = int.Parse(a[i + 1]); break; default: throw new GuiException("InvalidCommand"); } }
        if (targetMachine == null || mode is not ("control" or "observe") || idle is < 1 or > 86400) throw new GuiException("InvalidSessionOptions"); command = new("session-open", MachineId: targetMachine, Mode: mode, IdleSeconds: idle);
    }
    else throw new GuiException("InvalidCommand");
    var reply = await ControllerHost.Call(c, command); Console.WriteLine(GuiProtocol.Encode(reply)); return reply.ExitCode;
}
catch (Exception e) { var reply = GuiResponse.Reject(e is GuiException g ? g.Code : !File.Exists(Path.Combine(AppContext.BaseDirectory, "Controller.json")) ? "InvalidConfiguration" : "InvalidRequest"); Console.WriteLine(GuiProtocol.Encode(reply)); return reply.ExitCode; }

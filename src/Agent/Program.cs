using System.IO;
using System.Diagnostics;
using System.Text;
using DesktopTestDriver;
internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
        try { return Run(args).GetAwaiter().GetResult(); }
        catch (Exception e) { Console.Error.WriteLine(GuiProtocol.Encode(new { code = e is GuiException g ? g.Code : "AgentUnavailable", detail = e.Message })); return 1; }
    }
    static async Task<int> Run(string[] args)
    {
        WindowsDesktop.EnableDpi();
        var internalExit = InternalDesktopCommands.TryRun(args); if (internalExit.HasValue) return internalExit.Value;
        var list = args.ToList();
        if (list is ["--help"] or ["-h"])
        {
            Console.WriteLine($"""
                DesktopTestDriver Agent {typeof(AgentHost).Assembly.GetName().Version?.ToString(3)} (wire {GuiProtocol.WireVersion})
                Trusted networks and PCs only. The connection is not encrypted or authenticated.

                  [--serve] [--controller HOST:PORT] [--machine NAME] [--state DIR]   default 127.0.0.1:{HostSettings.DefaultPort}, machine = computer name
                  --stop [--state DIR]
                  --environment
                Run it in a logged-on desktop session. Run it elevated to drive elevated tools (regedit, services.msc).
                Usage guide: {Path.Combine(AppContext.BaseDirectory, "docs", "agent-usage.md")}
                """);
            return 0;
        }
        if (list is ["--environment"])
        {
            using var process = Process.GetCurrentProcess(); using var environment = new WindowsDesktop(process.SessionId, Environment.ProcessPath!, Path.GetTempPath());
            Console.WriteLine(GuiProtocol.Encode(new GuiResponse("completed", Data: new { valid = environment.Valid, sessionId = process.SessionId, sessionKind = WindowsDesktop.SessionKind(process.SessionId), elevated = Processes.CurrentElevated })));
            return 0;
        }
        var settings = HostSettings.Agent(list);
        if (list is ["--stop"])
        {
            var stopped = AgentHost.RequestStop(settings.StateDirectory);
            Console.WriteLine(GuiProtocol.Encode(stopped ? new GuiResponse("completed") : GuiResponse.Reject("AgentUnavailable"))); return stopped ? 0 : 1;
        }
        if (!(list.Count == 0 || list is ["--serve"])) throw new GuiException("InvalidCommand", "Unknown option. See --help");
        using var current = Process.GetCurrentProcess();
        settings = settings with { InteractiveSessionId = settings.InteractiveSessionId == 0 ? current.SessionId : settings.InteractiveSessionId };
        using var desktop = new WindowsDesktop(settings.InteractiveSessionId, Environment.ProcessPath!, Path.Combine(settings.StateDirectory, "capture-work"));
        if (!desktop.Valid) throw new GuiException("NoInteractiveSession", "Start the agent inside a logged-on desktop session");
        using var stop = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        await new AgentHost(settings, desktop, new WindowsInput()).Run(Environment.ProcessPath!, stop.Token); return 0;
    }
}

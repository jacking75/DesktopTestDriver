using System.IO;
using System.Diagnostics;
using System.Text;
using DesktopTestDriver;
internal static class Program
{
    [STAThread] static int Main(string[] args) { Console.InputEncoding = new UTF8Encoding(false, true); Console.OutputEncoding = new UTF8Encoding(false); try { return Run(args).GetAwaiter().GetResult(); } catch (Exception e) { Console.Error.WriteLine(GuiProtocol.Encode(new { code = e is GuiException g ? g.Code : "AgentUnavailable", detail = e is GuiException ? e.Message : e.GetType().Name, hresult = e.HResult })); return 1; } }
    static async Task<int> Run(string[] args)
    {
        WindowsDesktop.EnableDpi(); var list = args.ToList(); var config = Path.Combine(AppContext.BaseDirectory, "Agent.json");
        if (list.Count >= 2 && list[0] == "--config") { config = Path.GetFullPath(list[1]); list.RemoveRange(0, 2); }
        var a = list.ToArray();
        if (a is ["--help"]) { Console.WriteLine($"DesktopTestDriver Agent / GUI API 1/2\n--environment | --config FILE --serve | --stop\n--config NEW_FILE --initialize STATE MACHINE HOST PORT CONTROLLER.cer\nInternal: --watchdog STATE | --uia HWND\nUsage: {Path.Combine(AppContext.BaseDirectory, "docs", "agent-usage.md")}"); return 0; }
        var internalExit=await InternalDesktopCommands.TryRun(a); if(internalExit.HasValue) return internalExit.Value;
        if (a is ["--initialize", var state, var machine, var host, var port, var controllerCert])
        {
            if (File.Exists(config)) throw new GuiException("ConfigurationExists"); using var cert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile(controllerCert);
            var settings = new AgentSettings(Path.GetFullPath(state), machine, host, int.Parse(port), ConnectionSettings.Hash(cert)); ConnectionSettings.Validate(settings);
            var store = new StateStore(settings.StateDirectory); ConnectionSettings.Initialize(store, machine);
            File.WriteAllText(config, GuiProtocol.Encode(settings)); Console.WriteLine(GuiProtocol.Encode(new GuiResponse("completed", Data: new { configuration = config, publicCertificate = store.PathFor("identity.cer") }))); return 0;
        }
        if(a is ["--environment"])
        {
            using var process=Process.GetCurrentProcess();using var environment=new WindowsDesktop(process.SessionId,Environment.ProcessPath!,Path.GetTempPath());
            Console.WriteLine(GuiProtocol.Encode(new GuiResponse("completed",Data:new {valid=environment.Valid,sessionId=process.SessionId,sessionKind=WindowsDesktop.SessionKind(process.SessionId),desktop=environment.Valid?"Default":"unavailable"})));return 0;
        }
        var c = ConnectionSettings.Agent(config); var stateStore = new StateStore(c.StateDirectory);
        if (a is ["--register-profile", var profileFile, var bindingsFile])
        {
            var registered = new ProfileRegistry(stateStore).Register(File.ReadAllText(profileFile), GuiProtocol.Decode<MachineBindings>(File.ReadAllText(bindingsFile)));
            Console.WriteLine(GuiProtocol.Encode(new GuiResponse("completed", Data: new { profileId = registered.Profile.ProfileId, registered.ProfileHash }))); return 0;
        }
        if (a is ["--qualify-profile", var qualificationFile])
        {
            new ProfileRegistry(stateStore).Qualify(GuiProtocol.Decode<BackgroundQualification>(File.ReadAllText(qualificationFile)));
            Console.WriteLine(GuiProtocol.Encode(new GuiResponse("completed"))); return 0;
        }
        if (a is ["--stop"]) { var identity = stateStore.Read<DesktopProcess>("agent-process.json"); if (!Processes.Alive(identity)) throw new GuiException("AgentUnavailable"); stateStore.Write("agent-stop.json", identity); return 0; }
        if (!(a.Length == 0 || a is ["--serve"])) throw new GuiException("InvalidCommand");
        using var current = Process.GetCurrentProcess(); var session = c.InteractiveSessionId == 0 ? current.SessionId : c.InteractiveSessionId; c = c with { InteractiveSessionId = session };
        using var desktop = new WindowsDesktop(session, Environment.ProcessPath!, c.StateDirectory); if (!desktop.Valid) throw new GuiException("NoInteractiveSession");
        using var stop = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); }; await new AgentHost(c, desktop, new WindowsInput()).Run(Environment.ProcessPath!, stop.Token); return 0;
    }
}

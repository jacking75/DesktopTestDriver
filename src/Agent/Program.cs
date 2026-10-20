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
        if (a is ["--help"]) { Console.WriteLine($"DesktopTestDriver Agent / GUI API 1\n--config FILE --serve | --stop\n--config NEW_FILE --initialize STATE MACHINE HOST PORT CONTROLLER.cer\nInternal: --watchdog STATE | --uia HWND\nUsage: {Path.Combine(AppContext.BaseDirectory, "docs", "agent-usage.md")}"); return 0; }
        if (a is ["--watchdog", var root]) return await WatchdogRunner.Run(root);
        if (a is ["--uia", var hwnd]) { var elements = await Task.Run(() => WindowsDesktop.ReadControls(long.Parse(hwnd, System.Globalization.CultureInfo.InvariantCulture))); Console.WriteLine(GuiProtocol.Encode(elements)); return 0; }
        if (a is ["--capture", var captureHandle, var capturePid, var captureStart, var captureOutput, var cx, var cy, var cw, var ch]) { await GraphicsCapture.Save(long.Parse(captureHandle, System.Globalization.CultureInfo.InvariantCulture), int.Parse(capturePid, System.Globalization.CultureInfo.InvariantCulture), long.Parse(captureStart, System.Globalization.CultureInfo.InvariantCulture), new(int.Parse(cx, System.Globalization.CultureInfo.InvariantCulture), int.Parse(cy, System.Globalization.CultureInfo.InvariantCulture), int.Parse(cw, System.Globalization.CultureInfo.InvariantCulture), int.Parse(ch, System.Globalization.CultureInfo.InvariantCulture)), captureOutput); return 0; }
        if (a is ["--focus", var focusHandle, var focusPid, var focusStart]) return WindowsDesktop.ActivateWindow(long.Parse(focusHandle, System.Globalization.CultureInfo.InvariantCulture), int.Parse(focusPid, System.Globalization.CultureInfo.InvariantCulture), long.Parse(focusStart, System.Globalization.CultureInfo.InvariantCulture)) ? 0 : 1;
        if (a is ["--initialize", var state, var machine, var host, var port, var controllerCert])
        {
            if (File.Exists(config)) throw new GuiException("ConfigurationExists"); using var cert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile(controllerCert);
            var store = new StateStore(Path.GetFullPath(state)); ConnectionSettings.Initialize(store, machine); var settings = new AgentSettings(store.Root, machine, host, int.Parse(port), ConnectionSettings.Hash(cert));
            File.WriteAllText(config, GuiProtocol.Encode(settings)); Console.WriteLine(GuiProtocol.Encode(new GuiResponse("completed", Data: new { configuration = config, publicCertificate = store.PathFor("identity.cer") }))); return 0;
        }
        var c = ConnectionSettings.Agent(config); var stateStore = new StateStore(c.StateDirectory);
        if (a is ["--stop"]) { var identity = stateStore.Read<DesktopProcess>("agent-process.json"); if (!Processes.Alive(identity)) throw new GuiException("AgentUnavailable"); stateStore.Write("agent-stop.json", identity); return 0; }
        if (!(a.Length == 0 || a is ["--serve"])) throw new GuiException("InvalidCommand");
        using var current = Process.GetCurrentProcess(); var session = c.InteractiveSessionId == 0 ? current.SessionId : c.InteractiveSessionId; c = c with { InteractiveSessionId = session };
        using var desktop = new WindowsDesktop(session, Environment.ProcessPath!, c.StateDirectory); if (!desktop.Valid) throw new GuiException("NoInteractiveSession");
        using var stop = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); }; await new AgentHost(c, desktop, new WindowsInput()).Run(Environment.ProcessPath!, stop.Token); return 0;
    }
}

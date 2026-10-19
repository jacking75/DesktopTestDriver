using System.Diagnostics;
using DesktopTestDriver;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Wire.ConfigureConsole();
            WindowsDesktop.EnableDpi();
            if (args.Length == 0)
                return RunAgent(Path.Combine(AppContext.BaseDirectory, "Agent.json")).GetAwaiter().GetResult();
            if (args is ["--agent", var configuration])
            {
                return RunAgent(configuration).GetAwaiter().GetResult();
            }
            if (args.Length == 2 && args[0] == "--uia")
            {
                Console.WriteLine(Wire.Encode(WindowsDesktop.ReadControls(long.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture))));
                return 0;
            }
            if (args.Length != 2 || args[0] != "--run") throw new DriverException(Error.InvalidRequest);
            return Run(args[1]).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(Wire.Encode(new { errorCode = e is DriverException d ? d.Code : Error.ObservationUnavailable }));
            return 1;
        }
    }
    private static async Task<int> RunAgent(string configuration)
    {
        var config = ApplicationStartup.Agent(configuration);
        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };
        await using var agent = new PersistentAgent(config, Environment.ProcessPath!);
        await agent.Run(stopping.Token);
        return 0;
    }
    private static async Task<int> Run(string path)
    {
        var store = new RunStore(path);
        if (!WindowsDesktop.ConsoleValid(store.Config.TargetSession) || store.Stopped || File.Exists(store.PathFor("client.json")) || File.Exists(store.PathFor("status.json")) ||
            Directory.GetFiles(store.Root, "command-*.json").Length != 0) throw new DriverException(Error.SessionChanged);
        if (store.Config.ExpiresAt <= DateTimeOffset.UtcNow) throw new DriverException(Error.Expired);
        using var once = new FileStream(store.PathFor("client.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var identity = Processes.Identify(Process.GetCurrentProcess());
        store.Write("client.json", identity);
        store.Pulse("client-heartbeat");
        // A single initial startup grace covers task scheduling, never a restart/reconnect or a new run deadline.
        store.Pulse("server-heartbeat");
        var watchdogExecutable = Path.Combine(AppContext.BaseDirectory, "..", "Client.Watchdog", "Client.Watchdog.exe");
        using var watchdog = Processes.Start(Path.GetFullPath(watchdogExecutable), "--run", store.Root);
        var watchdogIdentity = Processes.Identify(watchdog);
        var desktop = new WindowsDesktop(store.Config, Environment.ProcessPath!);
        using var monitor = store.Config.AllowInput ? desktop.MonitorInput(() => InputConflictPolicy.ExternalInputAllowed(store)) : null;
        var input = new InputController(store, new WindowsInput());
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; store.Stop(Error.Stopped); };
        bool WatchdogAlive() => Processes.Alive(watchdogIdentity) && File.Exists(store.PathFor("watchdog.json")) && store.Read<ProcessIdentity>("watchdog.json") == watchdogIdentity;
        var wait = Stopwatch.GetTimestamp();
        while (!WatchdogAlive() && !watchdog.HasExited && Stopwatch.GetElapsedTime(wait).TotalSeconds < 2) await Task.Delay(10);
        if (!WatchdogAlive()) { store.Stop(Error.WatchdogUnavailable); input.Cleanup(); return 1; }
        var status = new ClientStatus(identity, watchdogIdentity, store.Config.TargetSession, WindowsDesktop.DesktopName(), store.ManifestHash, store.PipeName);
        store.Write("status.json", status);
        using var engine = new CommandEngine(store, desktop, input, WatchdogAlive);
        // Network runs accept operational commands only on the direct controller connection.
        var server = File.Exists(store.PathFor("controller.json"))
            ? ClientController.Run(store, engine, status, store.Read<ControllerConnection>("controller.json"), cancellation.Token)
            : PipeRelay.Serve(store, engine, cancellation.Token);
        try
        {
            while (!store.Stopped)
            {
                if (!WatchdogAlive()) { store.Stop(Error.WatchdogUnavailable); break; }
                if (!WindowsDesktop.ConsoleValid(store.Config.TargetSession)) { store.Stop(Error.SessionChanged); break; }
                if (desktop.PhysicalConflict) { store.Stop(Error.UserInputConflict); break; }
                store.Pulse("client-heartbeat");
                await Task.Delay(50);
            }
        }
        catch { store.Stop(Error.ObservationUnavailable); }
        finally
        {
            store.Stop(Error.Stopped);
            bool clean = input.Cleanup();
            store.Write("client-cleanup.json", new { clean, at = DateTimeOffset.UtcNow });
            // Leave a brief drain window so a stop response reaches the Broker.
            await Task.Delay(100);
            await Task.WhenAny(watchdog.WaitForExitAsync(), Task.Delay(2000));
            cancellation.Cancel();
            await server;
        }
        return File.Exists(store.PathFor("cleanup.json")) && store.Read<InputLedger>("ledger.json").Held.Length == 0 ? 0 : 1;
    }
}

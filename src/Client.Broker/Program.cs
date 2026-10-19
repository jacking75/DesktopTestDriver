using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using DesktopTestDriver;

try
{
    Wire.ConfigureConsole();
    if (args.Length < 2) throw new DriverException(Error.InvalidRequest);
    switch (args[0])
    {
        case "agent-prepare" when args.Length == 3:
        {
            var config = Wire.Decode<AgentConfiguration>(File.ReadAllText(args[2])); config.Validate();
            if (!Path.GetFullPath(config.StateRoot).Equals(Path.GetFullPath(args[1]), StringComparison.OrdinalIgnoreCase)) throw new DriverException(Error.DelegationMismatch);
            RunStore.CreatePrivateDirectory(Path.GetFullPath(args[1]));
            var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Client", "Agent.exe"));
            var argument = "--agent \"" + Path.GetFullPath(args[2]) + "\"";
            var task = "DesktopTestDriver-Agent-" + config.MachineId;
            File.WriteAllText(Path.Combine(config.StateRoot, "agent-task.json"), Wire.Encode(new { name = task, executable, argument }));
            string Q(string s) => "'" + s.Replace("'", "''") + "'";
            await PowerShell("$ErrorActionPreference='Stop'; $task=" + Q(task) + "; if(Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue){throw 'Conflict'}; " +
                "$a=New-ScheduledTaskAction -Execute " + Q(executable) + " -Argument " + Q(argument) + "; " +
                "$p=New-ScheduledTaskPrincipal -UserId " + Q(WindowsIdentity.GetCurrent().Name) + " -LogonType Interactive -RunLevel Limited; " +
                "$s=New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew; " +
                "Register-ScheduledTask -TaskName $task -Action $a -Principal $p -Settings $s | Out-Null; Start-ScheduledTask -TaskName $task");
            Console.WriteLine(Wire.Encode(new { prepared = true, ready = false, mode = "persistent-agent", config.MachineId }));
            break;
        }
        case "agent-rollback" when args.Length == 2:
        {
            RunStore.CheckPrivate(Path.GetFullPath(args[1]));
            var task = Wire.Decode<System.Text.Json.JsonElement>(File.ReadAllText(Path.Combine(args[1], "agent-task.json")));
            var statusFile = Path.Combine(args[1], "agent-status.json");
            if (File.Exists(statusFile))
            {
                var status = Wire.Decode<AgentStatus>(File.ReadAllText(statusFile));
                if (status.ActiveRun != null || status.CleanupBlocked) throw new DriverException(Error.CleanupIncomplete);
                if (!Processes.KillOwned(status.Agent)) throw new DriverException(Error.CleanupIncomplete);
            }
            string Q(string s) => "'" + s.Replace("'", "''") + "'";
            await PowerShell("$ErrorActionPreference='Stop'; $t=Get-ScheduledTask -TaskName " + Q(task.GetProperty("name").GetString()!) + " -ErrorAction SilentlyContinue; if($t){" +
                "if($t.Actions.Count -ne 1 -or $t.Actions[0].Execute -ne " + Q(task.GetProperty("executable").GetString()!) + " -or $t.Actions[0].Arguments -ne " + Q(task.GetProperty("argument").GetString()!) + "){throw 'Ownership'}; " +
                "Stop-ScheduledTask -InputObject $t; Unregister-ScheduledTask -InputObject $t -Confirm:$false}");
            Console.WriteLine(Wire.Encode(new { removed = true })); break;
        }
        case "prepare" when args.Length == 3:
        {
            var store = RunStore.Create(args[1], args[2]);
            var controllerPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[2]))!, "controller.json");
            if (File.Exists(controllerPath))
            {
                var connection = Wire.Decode<ControllerConnection>(File.ReadAllText(controllerPath));
                ControllerFiles.Validate(connection, store);
                store.Write("controller.json", connection);
            }
            var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Client", "Agent.exe"));
            if (!File.Exists(executable)) throw new DriverException(Error.InvalidRequest);
            var task = "DesktopTestDriver-" + store.Config.RunId;
            store.Write("task.json", new { name = task, executable, user = WindowsIdentity.GetCurrent().Name });
            string Q(string s) => "'" + s.Replace("'", "''") + "'";
            await PowerShell("$ErrorActionPreference='Stop'; $task=" + Q(task) + "; if(Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue){throw 'Conflict'}; " +
                "$a=New-ScheduledTaskAction -Execute " + Q(executable) + " -Argument " + Q("--run \"" + store.Root + "\"") + "; " +
                "$p=New-ScheduledTaskPrincipal -UserId " + Q(WindowsIdentity.GetCurrent().Name) + " -LogonType Interactive -RunLevel Limited; " +
                "$s=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 16) -MultipleInstances IgnoreNew; " +
                "Register-ScheduledTask -TaskName $task -Action $a -Principal $p -Settings $s | Out-Null; Start-ScheduledTask -TaskName $task");
            Console.WriteLine(Wire.Encode(new { prepared = true, ready = false, manifestHash = store.ManifestHash }));
            break;
        }
        case "status" when args.Length == 2:
        {
            var store = new RunStore(args[1]);
            var status = store.Read<ClientStatus>("status.json");
            if (!Processes.Alive(status.Client) || !Processes.Alive(status.Watchdog) || status.ManifestHash != store.ManifestHash || store.Stopped) throw new DriverException(Error.WatchdogUnavailable);
            Console.WriteLine(Wire.Encode(status));
            break;
        }
        case "send" when args.Length == 2:
        {
            var store = new RunStore(args[1]);
            using var bounded = new CancellationTokenSource(8000);
            var request = Wire.Decode<Command>(await Lines.Read(Console.In, bounded.Token) ?? throw new DriverException(Error.InvalidRequest));
            Console.WriteLine(Wire.Encode(await PipeRelay.Send(store, request, bounded.Token)));
            break;
        }
        case "stop" when args.Length == 2:
        {
            var store = new RunStore(args[1]);
            store.Stop(Error.Stopped);
            // Do not inject from the SSH session. The console watchdog owns release and cleanup.
            var began = Stopwatch.GetTimestamp();
            while (!File.Exists(store.PathFor("cleanup.json")) && Stopwatch.GetElapsedTime(began).TotalSeconds < 2) await Task.Delay(25);
            bool clean = false;
            if (File.Exists(store.PathFor("cleanup.json")))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(store.PathFor("cleanup.json")));
                clean = doc.RootElement.GetProperty("clean").GetBoolean() && doc.RootElement.GetProperty("normalDesktop").GetBoolean();
            }
            Console.WriteLine(Wire.Encode(new { clean }));
            if (!clean) return 1;
            break;
        }
        case "remove-task" when args.Length == 2:
        case "rollback" when args.Length == 2:
        {
            var store = new RunStore(args[1]);
            bool rollback = args[0] == "rollback";
            if (rollback) store.Stop(Error.Stopped);
            if (!store.Stopped || store.Read<InputLedger>("ledger.json") is not { Held.Length: 0, Processes.Length: 0 }) throw new DriverException(Error.CleanupIncomplete);
            if (rollback && Directory.GetFiles(store.Root, "command-*.json").Length != 0) throw new DriverException(Error.CleanupIncomplete);
            if (!rollback)
            {
                using var cleanup = System.Text.Json.JsonDocument.Parse(File.ReadAllText(store.PathFor("cleanup.json")));
                if (!cleanup.RootElement.GetProperty("clean").GetBoolean() || !cleanup.RootElement.GetProperty("normalDesktop").GetBoolean()) throw new DriverException(Error.CleanupIncomplete);
            }
            var task = "DesktopTestDriver-" + store.Config.RunId;
            var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Client", "Agent.exe"));
            string Q(string s) => "'" + s.Replace("'", "''") + "'";
            await PowerShell("$ErrorActionPreference='Stop'; $t=Get-ScheduledTask -TaskName " + Q(task) + " -ErrorAction SilentlyContinue; if($t){" +
                "if($t.Actions.Count -ne 1 -or $t.Actions[0].Execute -ne " + Q(executable) + " -or $t.Actions[0].Arguments -ne " + Q("--run \"" + store.Root + "\"") + "){throw 'Ownership mismatch'}; " +
                "Stop-ScheduledTask -TaskName " + Q(task) + "; Unregister-ScheduledTask -TaskName " + Q(task) + " -Confirm:$false}");
            if (rollback)
            {
                foreach (var file in new[] { "client.json", "watchdog.json" })
                    if (File.Exists(store.PathFor(file)) && !Processes.KillOwned(store.Read<ProcessIdentity>(file))) throw new DriverException(Error.CleanupIncomplete);
            }
            Console.WriteLine(Wire.Encode(new { removed = true }));
            break;
        }
        default: throw new DriverException(Error.InvalidRequest);
    }
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine(Wire.Encode(new { errorCode = e is DriverException d ? d.Code : Error.ObservationUnavailable }));
    return 1;
}

static async Task PowerShell(string script)
{
    var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
    using var process = Process.Start(start) ?? throw new DriverException(Error.ObservationUnavailable);
    var output = process.StandardOutput.ReadToEndAsync();
    var errors = process.StandardError.ReadToEndAsync();
    using var limit = new CancellationTokenSource(5000);
    try { await process.WaitForExitAsync(limit.Token); }
    catch { process.Kill(true); throw new DriverException(Error.Expired); }
    await Task.WhenAll(output, errors);
    if (process.ExitCode != 0) throw new DriverException(Error.ObservationUnavailable);
}

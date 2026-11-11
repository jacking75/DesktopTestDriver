using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;

namespace DesktopTestDriver;

public sealed record AgentHello(string MachineId, int WireVersion);
public sealed record OpenSession(string Mode, bool Input, int IdleSeconds, TargetSpec? Targets);
/// <summary>Runs on the machine under test. Connects to the controller over plain TCP and serves its requests.</summary>
public sealed class AgentHost
{
    private readonly AgentSettings settings;
    private readonly StateStore store;
    private readonly IDisposable singleInstance;
    private readonly IGuiDesktop desktop;
    private readonly GuiInput input;
    private readonly ControlSessions sessions;
    private readonly GuiRequestHandler handler;
    private readonly long startedAt = Stopwatch.GetTimestamp();
    public AgentHost(AgentSettings settings, IGuiDesktop desktop, IInputBackend backend, IActivityMonitor? monitor = null)
    {
        this.settings = settings; this.desktop = desktop; store = new(settings.StateDirectory); singleInstance = store.Lock("agent.lock", 1);
        try
        {
            input = new(store, backend);
            sessions = new(store, settings.MachineId); sessions.Recover();
            handler = new(store, desktop, sessions, input, settings.MaxStopMilliseconds)
            { MachineId = settings.MachineId, Interaction = backend is WindowsInput || monitor != null ? new InteractionGuard(monitor) : null };
        }
        catch { singleInstance.Dispose(); throw; }
    }
    public GuiRequestHandler Handler => handler;
    private object Status() => new
    {
        machineId = settings.MachineId,
        agentRunning = true,
        wireVersion = GuiProtocol.WireVersion,
        version = typeof(AgentHost).Assembly.GetName().Version?.ToString(3),
        interactiveSessionId = settings.InteractiveSessionId,
        desktop = desktop.Valid ? "Default" : "unavailable",
        elevated = Processes.CurrentElevated,
        activityMonitorState = handler.Interaction?.State ?? "simulated",
        maxRequestBytes = GuiProtocol.MaxRequestBytes,
        maxCommandMilliseconds = GuiProtocol.MaxCommandMilliseconds,
        maxWaitMilliseconds = GuiProtocol.MaxWaitMilliseconds,
        maxHoldMilliseconds = settings.MaxHoldMilliseconds,
        journal = new { entries = handler.Journal.Count, bytes = handler.Journal.Bytes, maxBytes = RequestJournal.MaxJournalBytes },
        captures = new { bytes = handler.Captures.Bytes, maxBytes = CaptureStore.MaxBytes },
        sessions = sessions.List()
    };
    private object Doctor()
    {
        var items = new List<DiagnosticItem>();
        void Add(string name, Func<(bool Ok, string Reason, string? Next)> check)
        {
            try { var (ok, reason, next) = check(); items.Add(new(name, ok ? "ok" : "problem", reason, next)); }
            catch (Exception e) { items.Add(new(name, "problem", e is GuiException g ? g.Code + ": " + g.Message : e.GetType().Name + ": " + e.Message)); }
        }
        Add("interactiveDesktop", () => (desktop.Valid, desktop.Valid ? "Default desktop of a connected session" : "No interactive desktop (locked, disconnected or service session)", desktop.Valid ? null : "Run the agent in a logged-on desktop session"));
        Add("uiaWorker", () =>
        {
            var probe = "doctor-" + Guid.NewGuid().ToString("N")[..8]; var timer = Stopwatch.StartNew();
            try
            {
                var w = desktop.Windows().FirstOrDefault(x => x.Kind == "main" && !x.Minimized) ?? throw new GuiException("WindowNotFound", "No visible window to probe");
                using var limit = new CancellationTokenSource(5000);
                desktop.Automation(probe, new UiaCommand(w, "inspect", JsonSerializer.SerializeToElement(new { maxDepth = 1 })), limit.Token);
                return (true, "UIA worker started and answered in " + timer.ElapsedMilliseconds + " ms", null);
            }
            finally { desktop.ReleaseAutomation(probe); }
        });
        Add("graphicsCapture", () => { var ok = Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported(); return (ok, ok ? "Windows.Graphics.Capture supported" : "Windows.Graphics.Capture unsupported", ok ? null : "Use Windows 10 1903 or later"); });
        Add("inputMonitor", () => (handler.Interaction == null || handler.Interaction.State == "monitoring", "Human input hook: " + (handler.Interaction?.State ?? "not used"), null));
        Add("storage", () => (handler.Journal.Bytes < RequestJournal.MaxJournalBytes * 0.8 && handler.Captures.Bytes < CaptureStore.MaxBytes * 0.8,
            $"journal {handler.Journal.Bytes / 1024} KiB of {RequestJournal.MaxJournalBytes / 1048576} MiB, captures {handler.Captures.Bytes / 1024} KiB of {CaptureStore.MaxBytes / 1048576} MiB", null));
        Add("elevation", () =>
        {
            var uac = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", 1);
            return (true, "agent " + (Processes.CurrentElevated ? "elevated" : "not elevated") + ", UAC " + (uac is 0 ? "off" : "on"), Processes.CurrentElevated ? null : "Elevated targets (regedit, services.msc) need an elevated agent");
        });
        Add("sessions", () =>
        {
            var problems = sessions.List().Where(s => s.State == "blocked").ToArray();
            return (problems.Length == 0, problems.Length == 0 ? "no blocked sessions" : string.Join("; ", problems.Select(s => s.ControlSessionId + ": " + (s.PendingUnknownRequestIds.Length > 0 ? "unconfirmed requests " + string.Join(",", s.PendingUnknownRequestIds) : s.PauseReason ?? s.StopReason))),
                problems.Length == 0 ? null : "Reconcile unconfirmed requests (--session-reconcile ... --resume) or close the session");
        });
        Add("monitors", () =>
        {
            var monitors = new List<string>();
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr d, ref Native.NativeRect r, IntPtr x) =>
            {
                Native.GetDpiForMonitor(m, 0, out var dpiX, out _); monitors.Add($"{r.Right - r.Left}x{r.Bottom - r.Top}@{r.Left},{r.Top} dpi={dpiX}"); return true;
            }, IntPtr.Zero);
            return (monitors.Count > 0, string.Join("; ", monitors), null);
        });
        Add("version", () => (true, "agent " + typeof(AgentHost).Assembly.GetName().Version?.ToString(3) + ", wire " + GuiProtocol.WireVersion, null));
        return new { machineId = settings.MachineId, diagnostics = items };
    }
    private async Task<object> Dispatch(string method, JsonElement payload)
    {
        switch (method)
        {
            case "status": return new GuiResponse("completed", Data: Status());
            case "doctor": return new GuiResponse("completed", Data: Doctor());
            case "heartbeat": return new GuiResponse("completed");
            case "open":
            {
                if (!desktop.Valid) return GuiResponse.Reject("NoInteractiveSession");
                var request = payload.Deserialize<OpenSession>(GuiProtocol.Json)!;
                var opened = sessions.Open(request.Mode, request.Input, request.IdleSeconds, request.Targets);
                var session = sessions.Get(opened.ControlSessionId);
                try { handler.Interaction?.Arm(session); sessions.Save(session); }
                catch { await handler.Release(session, true, "LeaseAdmissionFailed"); throw; }
                if (request.Mode == "control" || request.Mode == "observe") desktop.StartAutomation(opened.ControlSessionId);
                return new GuiResponse("completed", ControlSessionId: opened.ControlSessionId, Data: session.Info);
            }
            case "session-pause":
            {
                var id = GuiProtocol.Text(payload, "sessionId"); sessions.Pause(id, "ExplicitPause"); await handler.Release(sessions.Get(id), false, "ExplicitPause");
                return new GuiResponse("completed", ControlSessionId: id, Data: sessions.Get(id).Info);
            }
            case "session-resume":
            {
                var id = GuiProtocol.Text(payload, "sessionId");
                var resumed = sessions.Resume(id, payload.TryGetProperty("expectedRevision", out var rev) && rev.ValueKind == JsonValueKind.Number ? rev.GetInt64() : null);
                var session = sessions.Get(id);
                try { handler.Interaction?.Arm(session); sessions.Save(session); }
                catch { sessions.Pause(id, "LeaseAdmissionFailed"); throw; }
                return new GuiResponse("completed", ControlSessionId: id, Data: session.Info);
            }
            case "session-reconcile":
            {
                var id = GuiProtocol.Text(payload, "sessionId");
                long? revision = payload.TryGetProperty("expectedRevision", out var rev) && rev.ValueKind == JsonValueKind.Number ? rev.GetInt64() : null;
                var info = sessions.Reconcile(id, GuiProtocol.Text(payload, "requestId"), GuiProtocol.Text(payload, "resolution"), GuiProtocol.OptionalText(payload, "evidenceRef"), revision);
                if (GuiProtocol.Flag(payload, "resume") && info.PendingUnknownRequestIds.Length == 0)
                {
                    info = sessions.Resume(id, null); var session = sessions.Get(id);
                    try { handler.Interaction?.Arm(session); sessions.Save(session); info = session.Info; } catch (GuiException) { }
                }
                return new GuiResponse("completed", ControlSessionId: id, Data: info);
            }
            case "sessions": return new GuiResponse("completed", Data: new { sessions = sessions.List(GuiProtocol.Flag(payload, "all")) });
            case "execute":
            {
                var command = payload.Deserialize<AgentCommand>(GuiProtocol.Json)!;
                var response = await handler.Execute(command);
                try { _ = GuiWire.Frame(response); }
                catch (GuiException e) when (e.Code == "ResponseTooLarge")
                {
                    // Reads can be retried with a smaller scope; a mutation keeps its outcome and drops only the data.
                    response = response.Status == "completed" && GuiProtocol.ReadOnly(command.Request.Operation)
                        ? response with { Status = "rejected", Data = null, Error = new("ResponseTooLarge", e.Message + " (use maxDepth, rootElementId or interactiveOnly)") }
                        : response with { Data = new { responseTooLarge = true } };
                }
                return response;
            }
            case "result": return handler.Result(GuiProtocol.Text(payload, "sessionId"), GuiProtocol.Text(payload, "requestId"));
            case "close":
            case "cancel":
            {
                var session = sessions.Get(GuiProtocol.Text(payload, "sessionId"));
                var clean = await handler.Release(session, method == "close", method == "close" ? "SessionClosed" : "RequestTimeout");
                return new GuiResponse(clean ? "completed" : "completionUnknown", ControlSessionId: session.Info.ControlSessionId, Data: session.Info,
                    Error: clean ? null : new("CleanupIncomplete", "CleanupIncomplete"));
            }
            case "chunk": return new { bytes = Convert.ToBase64String(handler.Captures.Chunk(GuiProtocol.Text(payload, "captureId"), payload.GetProperty("offset").GetInt64(), payload.GetProperty("size").GetInt32())) };
            case "capture-ack": handler.Captures.Acknowledge(GuiProtocol.Text(payload, "captureId")); return new GuiResponse("completed");
            default: return GuiResponse.Reject("UnsupportedOperation");
        }
    }
    private async Task<bool> CloseAll(string reason)
    {
        bool clean = true;
        foreach (var s in sessions.Active.Where(s => s.Info.State is not ("closed" or "expired")).ToArray())
            clean &= await handler.Release(s, true, reason);
        return clean;
    }
    private void Maintain(ref long lastCleanup)
    {
        if (Stopwatch.GetElapsedTime(lastCleanup).TotalSeconds < 60) return;
        lastCleanup = Stopwatch.GetTimestamp();
        handler.Journal.Cleanup(); sessions.Cleanup();
        var pendingCaptures = new HashSet<string>();
        foreach (var s in sessions.Active)
            foreach (var request in s.Info.PendingUnknownRequestIds)
                if (handler.Journal.Find(s.Info.ControlSessionId, request) is { Operation: "capture", AgentResponse: { Data: not null } agent } && GuiProtocol.Element(agent.Data).TryGetProperty("captureId", out var id))
                    pendingCaptures.Add(id.GetString()!);
        handler.Captures.Cleanup(pendingCaptures);
    }
    public async Task Run(string agentExecutable, CancellationToken cancellation, bool watchdog = true)
    {
        using var once = singleInstance;
        using var alive = new EventWaitHandle(false, EventResetMode.AutoReset, store.KernelName("alive"));
        using var stopRequested = new EventWaitHandle(false, EventResetMode.ManualReset, store.KernelName("stop"));
        using var watchdogStop = new EventWaitHandle(false, EventResetMode.ManualReset, store.KernelName("watchdog-stop"));
        stopRequested.Reset(); watchdogStop.Reset();
        store.Write("agent-process.json", Processes.Identify(Environment.ProcessId));
        // Sessions left by a previous run: stop their applications and release their input now.
        await CloseAll("AgentRestarted");
        using var watchdogProcess = watchdog ? Processes.Start(agentExecutable, "--watchdog", store.Root) : null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var stopWatcher = Task.Run(() => { WaitHandle.WaitAny([stopRequested, lifetime.Token.WaitHandle]); lifetime.Cancel(); });
        var maintenance = Task.Run(async () =>
        {
            long lastCleanup = 0;
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    alive.Set();
                    if (watchdogProcess is { HasExited: true }) { await CloseAll("WatchdogUnavailable"); lifetime.Cancel(); break; }
                    handler.Interaction?.Ping();
                    foreach (var s in sessions.Active.ToArray())
                    {
                        if (s.Info.State == "active" && s.Info.Input && handler.Interaction != null)
                        {
                            try { handler.Interaction.Check(s); }
                            catch (GuiException e) { try { sessions.Pause(s.Info.ControlSessionId, "InputLeaseRevoked: " + e.Message); } catch (GuiException) { } await handler.Release(s, false, "InputLeaseRevoked"); continue; }
                        }
                        // Blocked sessions expire too, so a stuck input session cannot hold the desktop forever.
                        if (s.Info.State is "active" or "blocked" && !s.CloseRequested && (!desktop.Valid || Stopwatch.GetElapsedTime(s.LastActivity).TotalSeconds >= s.Info.IdleSeconds))
                            await handler.Release(s, true, desktop.Valid ? "IdleExpired" : "DesktopChanged");
                    }
                    foreach (var id in input.Overdue(settings.MaxHoldMilliseconds)) await handler.Release(sessions.Get(id), false, "MaxHoldExceeded");
                    try { Maintain(ref lastCleanup); } catch (Exception e) { Console.Error.WriteLine(GuiProtocol.Encode(new { code = "CleanupFailed", detail = e.Message })); }
                    await Task.Delay(250, lifetime.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Console.Error.WriteLine(GuiProtocol.Encode(new { code = "MaintenanceFailure", detail = e.GetType().Name + ": " + e.Message })); lifetime.Cancel(); }
        }, CancellationToken.None);
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    using var tcp = new TcpClient { NoDelay = true }; using var connecting = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); connecting.CancelAfter(5000);
                    await tcp.ConnectAsync(settings.ControllerHost, settings.ControllerPort, connecting.Token);
                    tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    var stream = tcp.GetStream();
                    await GuiWire.Write(stream, new AgentHello(settings.MachineId, GuiProtocol.WireVersion), connecting.Token);
                    GuiResponse accepted;
                    try { accepted = await GuiWire.Read<GuiResponse>(stream, connecting.Token); }
                    catch (EndOfStreamException) { throw new GuiException("PeerHandshakeFailed"); }
                    if (accepted.Status != "completed") throw new GuiException(accepted.Error?.Code ?? "ConnectionRejected");
                    Console.Error.WriteLine(GuiProtocol.Encode(new { code = "Connected", controller = settings.ControllerHost + ":" + settings.ControllerPort, machineId = settings.MachineId }));
                    await using var peer = new RpcPeer(stream) { Handler = Dispatch };
                    var running = peer.Run(lifetime.Token);
                    try
                    {
                        while (!running.IsCompleted)
                        {
                            using var ping = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); ping.CancelAfter(2000);
                            await peer.Call<GuiResponse>("heartbeat", new { }, ping.Token);
                            await Task.WhenAny(running, Task.Delay(500, lifetime.Token));
                        }
                        await running;
                    }
                    finally { await CloseAll("ControllerDisconnected"); }
                }
                catch (Exception) when (lifetime.IsCancellationRequested) { break; }
                catch (Exception e)
                {
                    Console.Error.WriteLine(GuiProtocol.Encode(new { code = e is GuiException g ? g.Code : "ControllerUnavailable", detail = e.GetType().Name }));
                    await CloseAll("ControllerDisconnected"); await Task.Delay(500, lifetime.Token);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lifetime.Cancel(); var clean = await CloseAll("AgentStopped"); await maintenance; await stopWatcher; handler.Interaction?.Dispose();
            if (clean) watchdogStop.Set();
            if (watchdogProcess != null) await Task.WhenAny(watchdogProcess.WaitForExitAsync(), Task.Delay(1500));
        }
    }
    /// <summary>Asks a running agent that uses this state folder to stop.</summary>
    public static bool RequestStop(string stateDirectory)
    {
        var store = new StateStore(stateDirectory);
        try { using var e = EventWaitHandle.OpenExisting(store.KernelName("stop")); return e.Set(); }
        catch (WaitHandleCannotBeOpenedException) { return false; }
    }
}

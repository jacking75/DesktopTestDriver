using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace DesktopTestDriver;

public sealed record AgentHello(string MachineId, int InteractiveSessionId, string User, string Desktop);
public sealed class AgentHost
{
    private readonly AgentSettings settings;
    private readonly StateStore store;
    private readonly IDisposable singleInstance;
    private readonly IGuiDesktop desktop;
    private readonly GuiInput input;
    private readonly ControlSessions sessions;
    private readonly GuiRequestHandler handler;
    public AgentHost(AgentSettings settings, IGuiDesktop desktop, IInputBackend backend)
    {
        this.settings = settings; this.desktop = desktop; store = new(settings.StateDirectory); singleInstance = store.Lock("agent.lock", 1);
        try
        {
            input = new(store, backend);
            sessions = new(store, settings.MachineId, settings.InteractiveSessionId); sessions.Recover();
            handler = new(store, desktop, sessions, input, settings.MaxStopMilliseconds);
        }
        catch { singleInstance.Dispose(); throw; }
    }
    private object Status() => new
    {
        machineId = settings.MachineId,
        agentRunning = true,
        interactiveSessionId = settings.InteractiveSessionId,
        desktop = desktop.Valid ? "Default" : "unavailable",
        supportedOperations = GuiProtocol.Operations,
        captureTargets = new[] { "window" },
        apiVersion = 1,
        maxRequestBytes = GuiProtocol.MaxRequestBytes,
        maxCommandMilliseconds = 5000,
        maxStopMilliseconds = settings.MaxStopMilliseconds,
        requestRetentionSeconds = 86400,
        maxHoldMilliseconds = settings.MaxHoldMilliseconds,
        sessions = sessions.List(),
        inputAvailable = desktop.Valid
    };
    private async Task<object> Dispatch(string method, JsonElement payload)
    {
        switch (method)
        {
            case "status": return new GuiResponse("completed", Data: Status());
            case "heartbeat": return new GuiResponse("completed");
            case "open":
                if (!desktop.Valid) return GuiResponse.Reject("NoInteractiveSession");
                var opened = sessions.Open(GuiProtocol.Text(payload, "mode"), payload.GetProperty("idleSeconds").GetInt32());
                return new GuiResponse("completed", ControlSessionId: opened.ControlSessionId, Data: opened);
            case "sessions": return new GuiResponse("completed", Data: new { sessions = sessions.List() });
            case "execute": return await handler.Execute(GuiProtocol.Decode<AgentCommand>(payload.GetRawText()));
            case "result": return handler.Result(GuiProtocol.Text(payload, "sessionId"), GuiProtocol.Text(payload, "requestId"));
            case "close":
            case "cancel":
                var session = sessions.Get(GuiProtocol.Text(payload, "sessionId"));
                var clean = await handler.Release(session, method == "close", method == "close" ? "SessionClosed" : "RequestTimeout");
                return new GuiResponse(clean ? "completed" : "completionUnknown", ControlSessionId: session.Info.ControlSessionId, Data: session.Info,
                    Error: clean ? null : new("CleanupIncomplete", "CleanupIncomplete"), InputReleased: session.Info.InputReleased);
            case "chunk": return new { bytes = Convert.ToBase64String(handler.Captures.Chunk(GuiProtocol.Text(payload, "captureId"), payload.GetProperty("offset").GetInt64(), payload.GetProperty("size").GetInt32())) };
            default: return GuiResponse.Reject("UnsupportedOperation");
        }
    }
    private async Task<bool> CloseAll(string reason)
    {
        bool clean = true;
        foreach (var s in sessions.Active.Where(s => s.Info.State is not ("closed" or "expired")))
            clean &= await handler.Release(s, true, reason);
        return clean;
    }
    public async Task Run(string agentExecutable, CancellationToken cancellation)
    {
        using var once = singleInstance;
        if (store.Exists("watchdog-process.json"))
        {
            var previous = store.Read<DesktopProcess>("watchdog-process.json"); var waiting = Stopwatch.GetTimestamp();
            while (Processes.Alive(previous) && Stopwatch.GetElapsedTime(waiting).TotalMilliseconds < 3500) await Task.Delay(25, cancellation);
            if (Processes.Alive(previous)) throw new GuiException("PreviousRecoveryInProgress");
        }
        using var current = Process.GetCurrentProcess(); store.Write("agent-process.json", Processes.Identify(current));
        store.Write("agent-limits.json", settings.MaxHoldMilliseconds);
        if (store.Exists("worker-stop-needed.json")) File.Delete(store.PathFor("worker-stop-needed.json"));
        if (store.Exists("watchdog-stop.json")) File.Delete(store.PathFor("watchdog-stop.json"));
        store.Write("agent-heartbeat.json", Stopwatch.GetTimestamp());
        using var watchdog = Processes.Start(agentExecutable, "--watchdog", store.Root);
        var watchdogIdentity = Processes.Identify(watchdog); store.Write("watchdog-process.json", watchdogIdentity);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var heartbeat = Task.Run(async () =>
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    store.Write("agent-heartbeat.json", Stopwatch.GetTimestamp());
                    if (store.Exists("agent-stop.json") && store.Read<DesktopProcess>("agent-stop.json") == Processes.Identify(current)) lifetime.Cancel();
                    await Task.Delay(50, lifetime.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Console.Error.WriteLine(GuiProtocol.Encode(new { code = "HeartbeatStorageFailure", exceptionType = e.GetType().Name })); lifetime.Cancel(); }
        });
        var maintenance = Task.Run(async () =>
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    if (!Processes.Alive(watchdogIdentity)) { await CloseAll("WatchdogUnavailable"); lifetime.Cancel(); break; }
                    foreach (var s in sessions.Active.ToArray())
                    {
                        if (s.Info.State == "active" && (!desktop.Valid || Stopwatch.GetElapsedTime(s.LastActivity).TotalSeconds >= s.Info.IdleSeconds))
                            await handler.Release(s, true, desktop.Valid ? "IdleExpired" : "DesktopChanged");
                    }
                    foreach (var id in input.Overdue(Math.Max(50, settings.MaxHoldMilliseconds - 200))) await handler.Release(sessions.Get(id), false, "MaxHoldExceeded");
                    await Task.Delay(50, lifetime.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Console.Error.WriteLine(GuiProtocol.Encode(new { code = "MaintenanceFailure", exceptionType = e.GetType().Name, cause = e is GuiException g ? g.Code : null })); lifetime.Cancel(); }
        }, CancellationToken.None);
        try
        {
            using var certificate = ConnectionSettings.Certificate(store);
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    using var tcp = new TcpClient(); using var connecting = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); connecting.CancelAfter(5000);
                    await tcp.ConnectAsync(settings.ControllerHost, settings.ControllerPort, connecting.Token);
                    using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) => cert != null && ConnectionSettings.Hash(cert) == settings.ControllerCertificate &&
                        DateTime.UtcNow < cert.GetExpirationDateStringAsUtc());
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = settings.ControllerHost, ClientCertificates = new X509CertificateCollection { certificate }, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, connecting.Token);
                    await GuiWire.Write(ssl, new AgentHello(settings.MachineId, settings.InteractiveSessionId, System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, "Default"), connecting.Token);
                    var accepted = await GuiWire.Read<GuiResponse>(ssl, connecting.Token);
                    if (accepted.Status != "completed") throw new GuiException(accepted.Error?.Code ?? "ConnectionRejected");
                    await using var peer = new RpcPeer(ssl) { Handler = Dispatch };
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
                catch (Exception e) when (!lifetime.IsCancellationRequested)
                {
                    Console.Error.WriteLine(GuiProtocol.Encode(new { code = e is GuiException g ? g.Code : "ControllerDisconnected", exceptionType = e.GetType().Name }));
                    await CloseAll("ControllerDisconnected"); await Task.Delay(500, lifetime.Token);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lifetime.Cancel(); var clean = await CloseAll("AgentStopped"); await maintenance; await heartbeat;
            if (clean) store.Write("watchdog-stop.json", true);
            await Task.WhenAny(watchdog.WaitForExitAsync(), Task.Delay(1500));
        }
    }
}
internal static class CertificateDates
{
    public static DateTime GetExpirationDateStringAsUtc(this X509Certificate certificate) => DateTime.Parse(certificate.GetExpirationDateString(), System.Globalization.CultureInfo.CurrentCulture).ToUniversalTime();
}

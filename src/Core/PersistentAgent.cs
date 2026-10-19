using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;

namespace DesktopTestDriver;

public sealed record AgentConfiguration(string MachineId, string Host, int Port, int TargetSession, string StateRoot)
{
    public void Validate()
    {
        if (!Wire.Id(MachineId) || Uri.CheckHostName(Host) == UriHostNameType.Unknown || Port is < 25001 or > 25199 ||
            TargetSession <= 0 || !Path.IsPathFullyQualified(StateRoot) || StateRoot.StartsWith("\\\\", StringComparison.Ordinal))
            throw new DriverException(Error.InvalidRequest);
    }
}
public sealed record AgentStatus(string MachineId, ProcessIdentity Agent, int Session, string Desktop, string? ActiveRun, bool CleanupBlocked);
public sealed record AgentBegin(RunConfig Config, ControllerConnection Connection);
public sealed record AgentCleanup(bool Clean, bool NormalDesktop, string? RunId);

// This process stays connected across work sessions. Desktop/input remain in per-run Client + Watchdog.
public sealed class PersistentAgent(AgentConfiguration config, string executable) : IAsyncDisposable
{
    private readonly SemaphoreSlim lifecycle = new(1);
    private Process? worker;
    private ProcessIdentity? workerIdentity;
    private RunStore? store;
    private bool blocked;
    private AgentCleanup lastCleanup = new(true, true, null);
    private ProcessIdentity? identity;

    public async Task Run(CancellationToken cancellation)
    {
        config.Validate();
        // The kernel object lives for this process lifetime, without thread-affine ownership.
        // Global scope rejects other sessions, state paths and machine IDs on the same PC.
        using var machineSingleton = new Mutex(false, @"Global\DesktopTestDriver.Agent", out var firstAgent);
        if (!firstAgent) throw new DriverException(Error.ReservationConflict);
        if (!Directory.Exists(config.StateRoot)) RunStore.CreatePrivateDirectory(config.StateRoot);
        RunStore.CheckPrivate(config.StateRoot);
        if (!WindowsDesktop.ConsoleValid(config.TargetSession)) throw new DriverException(Error.SessionChanged);
        using var singleton = new FileStream(Path.Combine(config.StateRoot, "agent.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        identity = Processes.Identify(Process.GetCurrentProcess());
        WriteStatus();
        int delay = 250;
        while (!cancellation.IsCancellationRequested)
        {
            bool shutdown = false;
            using var socket = new TcpClient { NoDelay = true };
            try
            {
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellation); connect.CancelAfter(5000);
                await socket.ConnectAsync(config.Host, config.Port, connect.Token);
                using var channel = new ControllerChannel(socket.GetStream());
                await channel.Write(new("agent-hello", Data: Json(Status())), connect.Token);
                if ((await channel.Read(connect.Token)).Kind != "agent-ready") throw new DriverException(Error.InvalidRequest);
                delay = 250;
                using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                var requests = new List<Task>();
                try
                {
                    while (!connection.IsCancellationRequested)
                    {
                        using var receive = CancellationTokenSource.CreateLinkedTokenSource(connection.Token); receive.CancelAfter(4000);
                        var packet = await channel.Read(receive.Token);
                        if (packet.Kind is not ("agent-ping" or "agent-begin" or "agent-end" or "agent-shutdown") || !Wire.Id(packet.Id))
                            throw new DriverException(Error.InvalidRequest);
                        requests.RemoveAll(t => t.IsCompleted);
                        if (requests.Count >= 4) throw new DriverException(Error.InvalidRequest);
                        requests.Add(Handle(packet));
                    }
                }
                finally { connection.Cancel(); await Task.WhenAll(requests); }
                async Task Handle(ControllerPacket packet)
                {
                    try
                    {
                        object result;
                        if (packet.Kind == "agent-ping")
                        {
                            await ObserveExit(); result = Status();
                        }
                        else if (packet.Kind == "agent-begin")
                        {
                            if (!packet.Data.HasValue) throw new DriverException(Error.InvalidRequest);
                            var begin = Wire.Decode<AgentBegin>(packet.Data.Value.GetRawText());
                            result = await Begin(begin, connection.Token);
                        }
                        else
                        {
                            result = await End();
                            if (packet.Kind == "agent-shutdown") shutdown = true;
                        }
                        using var reply = new CancellationTokenSource(1000);
                        await channel.Write(new("agent-result", packet.Id, Data: Json(new ControllerReply(true, result))), reply.Token);
                        if (shutdown) connection.Cancel();
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        try
                        {
                            using var reply = new CancellationTokenSource(1000);
                            await channel.Write(new("agent-result", packet.Id, Data: Json(new ControllerReply(false, Error: e is DriverException d ? d.Code : Error.ObservationUnavailable))), reply.Token);
                        }
                        catch (Exception failure) when (failure is not OutOfMemoryException) { connection.Cancel(); }
                    }
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException) { /* Reconnect only the supervisor, never replay work. */ }
            finally { await End(); WriteStatus(); }
            if (shutdown || cancellation.IsCancellationRequested) break;
            try { await Task.Delay(delay, cancellation); } catch (OperationCanceledException) { break; }
            delay = Math.Min(5000, delay * 2);
        }
    }
    private AgentStatus Status()
    {
        string desktop;
        try { desktop = WindowsDesktop.DesktopName(); } catch { desktop = "Unavailable"; }
        return new(config.MachineId, identity ?? throw new DriverException(Error.InvalidRequest), config.TargetSession, desktop, store?.Config.RunId, blocked);
    }
    private void WriteStatus()
    {
        var temp = Path.Combine(config.StateRoot, Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(temp, Wire.Encode(Status()));
        File.Move(temp, Path.Combine(config.StateRoot, "agent-status.json"), true);
    }
    private static JsonElement Json(object data) => JsonSerializer.SerializeToElement(data, Wire.Json);
    private async Task<object> Begin(AgentBegin begin, CancellationToken cancellation)
    {
        await lifecycle.WaitAsync(cancellation);
        bool starting = false;
        try
        {
            if (blocked || store != null || worker != null) throw new DriverException(Error.ReservationConflict);
            starting = true;
            var run = begin.Config; run.Validate();
            if (run.MachineId != config.MachineId || run.TargetSession != config.TargetSession || run.ExpiresAt <= DateTimeOffset.UtcNow ||
                begin.Connection.Host != config.Host || begin.Connection.Port != config.Port || !WindowsDesktop.ConsoleValid(config.TargetSession))
                throw new DriverException(Error.DelegationMismatch);
            var root = Path.Combine(config.StateRoot, run.RunId);
            RunStore.CreatePrivateDirectory(root);
            var configFile = Path.Combine(root, "config.json");
            File.WriteAllText(configFile, Wire.Encode(run));
            File.WriteAllText(Path.Combine(root, "controller.json"), Wire.Encode(begin.Connection));
            store = RunStore.Create(Path.Combine(root, "state"), configFile);
            worker = Processes.Start(executable, "--run", store.Root);
            workerIdentity = Processes.Identify(worker);
            WriteStatus();
            var began = Stopwatch.GetTimestamp();
            while (!File.Exists(store.PathFor("controller-connected.json")))
            {
                cancellation.ThrowIfCancellationRequested();
                if (worker.HasExited || store.Stopped || Stopwatch.GetElapsedTime(began).TotalSeconds > 4) throw new DriverException(Error.TransportLost);
                await Task.Delay(20, cancellation);
            }
            return new { run.RunId, connected = true, status = store.Read<ClientStatus>("status.json") };
        }
        catch
        {
            // A failed begin is never repeated. Stop whatever was created before releasing the gate.
            if (starting) await EndLocked();
            throw;
        }
        finally { lifecycle.Release(); }
    }
    private async Task ObserveExit()
    {
        if (!await lifecycle.WaitAsync(0)) return;
        try { if (worker is { HasExited: true }) { await EndLocked(); WriteStatus(); } }
        finally { lifecycle.Release(); }
    }
    public async Task<AgentCleanup> End()
    {
        await lifecycle.WaitAsync();
        try { var result = await EndLocked(); if (identity != null) WriteStatus(); return result; }
        finally { lifecycle.Release(); }
    }
    private async Task<AgentCleanup> EndLocked()
    {
        if (store == null) return lastCleanup;
        store.Stop(Error.Stopped);
        if (worker is { HasExited: false })
        {
            using var limit = new CancellationTokenSource(3000);
            try { await worker.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException) { if (workerIdentity != null) Processes.KillOwned(workerIdentity); }
        }
        bool clean = false, normal = false;
        if (File.Exists(store.PathFor("cleanup.json")))
        {
            var receipt = store.Read<JsonElement>("cleanup.json");
            clean = receipt.GetProperty("clean").GetBoolean(); normal = receipt.GetProperty("normalDesktop").GetBoolean();
            var ledger = store.Read<InputLedger>("ledger.json");
            clean &= ledger.Held.Length == 0 && ledger.Processes.Length == 0 && (worker == null || worker.HasExited);
        }
        lastCleanup = new(clean, normal, store.Config.RunId);
        blocked = !clean || !normal;
        if (!blocked) { worker?.Dispose(); worker = null; workerIdentity = null; store = null; }
        return lastCleanup;
    }
    public async ValueTask DisposeAsync() { await End(); worker?.Dispose(); lifecycle.Dispose(); }
}

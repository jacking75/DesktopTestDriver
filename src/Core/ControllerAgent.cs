using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DesktopTestDriver;

public sealed class ControllerAgent(string root)
{
    private readonly ConcurrentDictionary<string, Peer> peers = new();
    private readonly ConcurrentDictionary<string, ManagedAgent> agents = new();
    private readonly ConcurrentDictionary<string, object> finished = new();
    private readonly ConcurrentDictionary<string, byte> activeMachines = new();
    private sealed record ScriptLease(CancellationTokenSource Stop, Dictionary<string, ManagedAgent> Agents);
    private readonly ConcurrentDictionary<string, ScriptLease> scripts = new();
    public async Task Serve(CancellationToken cancellation)
    {
        var settings = ControllerFiles.Settings(root);
        using var singleton = new FileStream(Path.Combine(root, "agent.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var listener = new TcpListener(IPAddress.Any, settings.Port);
        listener.Start(16);
        var api = ServeLocal(settings.PipeName, stop);
        var handlers = new List<Task>();
        using var slots = new SemaphoreSlim(128);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await slots.WaitAsync(stop.Token);
                TcpClient socket;
                try { socket = await listener.AcceptTcpClientAsync(stop.Token); }
                catch { slots.Release(); throw; }
                handlers.RemoveAll(t => t.IsCompleted);
                handlers.Add(Handle(socket));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            stop.Cancel(); listener.Stop();
            await Task.WhenAll(handlers); await api;
        }
        async Task Handle(TcpClient socket)
        {
            string? run = null, machine = null;
            using (socket)
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
            {
                handshake.CancelAfter(5000);
                try
                {
                    socket.NoDelay = true;
                    using var channel = new ControllerChannel(socket.GetStream());
                    var hello = await channel.Read(handshake.Token);
                    if (hello.Kind == "agent-hello")
                    {
                        var agentStatus = Wire.Decode<AgentStatus>(hello.Data?.GetRawText() ?? throw new DriverException(Error.InvalidRequest));
                        if (!Wire.Id(agentStatus.MachineId) || agentStatus.Agent.Pid <= 0 || agentStatus.Session <= 0 || agentStatus.Desktop != "Default") throw new DriverException(Error.InvalidRequest);
                        var agent = new ManagedAgent(agentStatus, channel);
                        if (!agents.TryAdd(agentStatus.MachineId, agent)) throw new DriverException(Error.ReservationConflict);
                        try
                        {
                            await channel.Write(new("agent-ready"), handshake.Token);
                            await agent.Run(stop.Token);
                        }
                        finally
                        {
                            agents.TryRemove(agentStatus.MachineId, out _);
                            // Also covers supervisor death while begin is in flight, before ActiveRun is acknowledged.
                            foreach (var owned in peers.Values.Where(p => p.MachineId == agentStatus.MachineId)) owned.Abort();
                        }
                        return;
                    }
                    var connection = hello.Hello ?? throw new DriverException(Error.InvalidRequest);
                    if (hello.Kind != "hello" || connection.TransportVersion != 1 || !Wire.Id(connection.RunId)) throw new DriverException(Error.InvalidRequest);
                    var enrollment = Wire.Decode<ControllerEnrollment>(File.ReadAllText(Path.Combine(root, connection.RunId + ".enrollment.json")));
                    var config = enrollment.Config;
                    if (config.MachineId != connection.MachineId || config.RunId != connection.RunId || enrollment.ManifestHash != connection.ManifestHash ||
                        config.ExpiresAt <= DateTimeOffset.UtcNow || hello.Status is not { } status || status.ManifestHash != enrollment.ManifestHash ||
                        status.Session != config.TargetSession || status.Desktop != config.ExpectedDesktop || status.Client.Pid <= 0 || status.Watchdog.Pid <= 0)
                        throw new DriverException(Error.DelegationMismatch);
                    if (!activeMachines.TryAdd(config.MachineId, 0)) throw new DriverException(Error.ReservationConflict);
                    machine = config.MachineId;
                    // Durable single-use enrollment: controller restart cannot reset a run or replay its commands.
                    using (var used = new FileStream(Path.Combine(root, config.RunId + ".used"), FileMode.CreateNew))
                        used.Write(Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("O")));
                    var peer = new Peer(config, enrollment.ManifestHash, status, channel);
                    if (!peers.TryAdd(config.RunId, peer)) throw new DriverException(Error.ReservationConflict);
                    run = config.RunId;
                    await channel.Write(new("ready"), handshake.Token);
                    await peer.Run(stop.Token);
                    finished[run] = new { runId = run, machineId = machine, connected = false, peer.Cleanup };
                    File.WriteAllText(Path.Combine(root, run + ".result.json"), Wire.Encode(finished[run]));
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    if (run != null) finished[run] = new { runId = run, machineId = machine, connected = false, error = "TransportLost", cleanup = (object?)null };
                    // Credentials and raw handshake/request bodies are never logged.
                }
                finally
                {
                    if (run != null) peers.TryRemove(run, out _);
                    if (machine != null) activeMachines.TryRemove(machine, out _);
                    slots.Release();
                }
            }
        }
    }
    private async Task ServeLocal(string pipeName, CancellationTokenSource stop)
    {
        var handlers = new List<Task>();
        using var slots = new SemaphoreSlim(8);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await slots.WaitAsync(stop.Token);
                var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(stop.Token); }
                catch { pipe.Dispose(); slots.Release(); throw; }
                handlers.RemoveAll(t => t.IsCompleted);
                handlers.Add(Handle(pipe));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { await Task.WhenAll(handlers); }
        async Task Handle(NamedPipeServerStream pipe)
        {
            using (pipe)
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
            {
                limit.CancelAfter(10000);
                try
                {
                    using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                    ControllerReply reply;
                    var shutdown = false;
                    try
                    {
                        var request = Wire.Decode<ControllerRequest>(await Lines.Read(reader, limit.Token) ?? throw new DriverException(Error.InvalidRequest));
                        if (request.Operation == "clients") reply = new(true, agents.Values.Select(p => p.Summary()).ToArray());
                        else if (request.Operation == "shutdown") { reply = new(true); shutdown = true; }
                        else if (request.Operation == "scenario" && request.Arguments is { } script)
                        {
                            var execution = Wire.Decode<ScriptExecution>(script.GetRawText()); execution.Validate();
                            limit.CancelAfter(TimeSpan.FromSeconds(execution.Plan.MaximumSeconds + 5));
                            using var life = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
                            using var watching = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
                            // The CLI owns this request's lifetime. Closing its pipe cancels the resident runner.
                            var disconnected = reader.ReadLineAsync(watching.Token).AsTask();
                            try
                            {
                                var running = RunScript(execution, life.Token);
                                if (await Task.WhenAny(running, disconnected) != running) life.Cancel();
                                reply = new(true, await running);
                            }
                            finally
                            {
                                watching.Cancel();
                                try { await disconnected; } catch (OperationCanceledException) { }
                            }
                        }
                        else if (request.RunId is { } id && request.Operation == "result" && finished.TryGetValue(id, out var result)) reply = new(true, result);
                        else reply = await Control(request, limit.Token);
                    }
                    catch (Exception e) when (e is not OutOfMemoryException) { reply = new(false, Error: e is DriverException d ? d.Code : Error.TransportLost); }
                    await writer.WriteLineAsync(Wire.Encode(reply).AsMemory(), limit.Token);
                    if (shutdown) stop.Cancel();
                }
                catch (Exception e) when (e is not OutOfMemoryException) { }
                finally { slots.Release(); }
            }
        }
    }
    public static async Task<ControllerReply> Call(string root, ControllerRequest request, CancellationToken cancellation)
    {
        var settings = ControllerFiles.Settings(root);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var seconds = request.Operation == "scenario" && request.Arguments is { } script
            ? Math.Clamp(Wire.Decode<ScriptExecution>(script.GetRawText()).Plan.MaximumSeconds, 20, 900) + 10 : 11;
        limit.CancelAfter(TimeSpan.FromSeconds(seconds));
        using var pipe = new NamedPipeClientStream(".", settings.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(limit.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        await writer.WriteLineAsync(Wire.Encode(request).AsMemory(), limit.Token);
        return Wire.Decode<ControllerReply>(await Lines.Read(reader, limit.Token) ?? throw new DriverException(Error.TransportLost));
    }

    private async Task<ScriptReport> RunScript(ScriptExecution execution, CancellationToken cancellation)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var held = new Dictionary<string, ManagedAgent>();
        var lease = new ScriptLease(stop, held);
        try
        {
            // Acquire every machine before beginning any work. Never wait while holding a partial reservation.
            foreach (var config in execution.Runs.Values.OrderBy(c => c.MachineId, StringComparer.Ordinal))
            {
                if (agents.ContainsKey(config.RunId)) throw new DriverException(Error.ReservationConflict);
                if (!agents.TryGetValue(config.MachineId, out var agent) || !agent.Operations.Wait(0)) throw new DriverException(Error.ReservationConflict);
                held.Add(config.MachineId, agent);
                if (agent.ActiveRun != null || agent.Status.CleanupBlocked || agent.Status.Session != config.TargetSession ||
                    activeMachines.ContainsKey(config.MachineId) || !scripts.TryAdd(config.MachineId, lease))
                    throw new DriverException(Error.ReservationConflict);
            }
            return await ScriptScenarioRunner.Run(root, execution, (request, token) => Control(request, token, lease), stop.Token);
        }
        finally
        {
            foreach (var (machine, agent) in held)
            {
                if (scripts.TryGetValue(machine, out var current) && ReferenceEquals(current, lease)) scripts.TryRemove(machine, out _);
                agent.Operations.Release();
            }
        }
    }

    private async Task<ControllerReply> Control(ControllerRequest request, CancellationToken cancellation, ScriptLease? lease = null)
    {
        var target = request.RunId ?? throw new DriverException(Error.InvalidRequest);
        var machine = agents.ContainsKey(target) ? target : peers.TryGetValue(target, out var targetPeer) ? targetPeer.MachineId : target;
        agents.TryGetValue(machine, out var agent);
        if (scripts.TryGetValue(machine, out var reservation))
        {
            if (lease == null && request.Operation == "end")
            {
                reservation.Stop.Cancel();
                return new(true, new { cancellationRequested = true, machineId = machine });
            }
            if (!ReferenceEquals(lease, reservation)) throw new DriverException(Error.ReservationConflict);
            if (agent == null || !lease!.Agents.TryGetValue(machine, out var owned) || !ReferenceEquals(agent, owned))
                throw new DriverException(Error.TransportLost);
        }
        else if (lease != null) throw new DriverException(Error.ReservationConflict);
        bool locked = false;
        // Manual stop must still bypass a queued ordinary request. A scenario is cancelled with end instead.
        bool emergency = request.Operation == "request" && request.Arguments is { } argument &&
            argument.TryGetProperty("name", out var name) && name.GetString() == "stop";
        try
        {
            if (agent != null && lease == null && !emergency)
            {
                await agent.Operations.WaitAsync(cancellation); locked = true;
            }
            if (request.Operation is "begin" or "end" or "shutdown-client" && agent != null)
            {
                if (request.Operation != "begin") return await agent.Call(request.Operation == "end" ? "agent-end" : "agent-shutdown", null, cancellation);
                var config = Wire.Decode<RunConfig>(request.Arguments?.GetRawText() ?? throw new DriverException(Error.InvalidRequest));
                config.Validate(); Authorization.ValidateDevelopment(config);
                if (config.MachineId != machine || config.TargetSession != agent.Status.Session || agent.Status.CleanupBlocked || agent.ActiveRun != null)
                    throw new DriverException(Error.ReservationConflict);
                var configPath = Path.Combine(root, config.RunId + ".config.json");
                using (var file = new FileStream(configPath, FileMode.CreateNew)) file.Write(Encoding.UTF8.GetBytes(Wire.Encode(config)));
                var connection = ControllerFiles.Enroll(root, configPath);
                var reply = await agent.Call("agent-begin", JsonSerializer.SerializeToElement(new AgentBegin(config, connection), Wire.Json), cancellation);
                if (reply.Ok) agent.ActiveRun = config.RunId;
                return reply;
            }
            if (request.Operation == "request" && request.Arguments is { } arguments)
            {
                var run = agent != null && target == machine ? agent.ActiveRun : target;
                if (run == null || !peers.TryGetValue(run, out var peer)) throw new DriverException(Error.TransportLost);
                return new(true, await peer.Request(arguments, cancellation));
            }
            throw new DriverException(Error.TransportLost);
        }
        finally { if (locked) agent!.Operations.Release(); }
    }
    private sealed class Peer(RunConfig config, string hash, ClientStatus status, ControllerChannel channel)
    {
        public string MachineId => config.MachineId;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<Result>> pending = new();
        private readonly SemaphoreSlim commands = new(1);
        private long sequence, heartbeatSequence;
        private volatile bool stopping;
        private CancellationTokenSource? lifetime;
        public void Abort() => lifetime?.Cancel();
        public JsonElement? Cleanup { get; private set; }
        public object Summary() => new { config.RunId, config.MachineId, connected = true, status, config.ExpiresAt };
        public async Task Run(CancellationToken cancellation)
        {
            using var life = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            life.CancelAfter(TimeSpan.FromSeconds(Math.Min(config.MaxSeconds + 3, Math.Max(.01, (config.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds + 3))));
            lifetime = life;
            var pump = Heartbeats(life);
            try
            {
                while (!life.IsCancellationRequested)
                {
                    var packet = await channel.Read(life.Token);
                    if (packet.Kind == "cleanup") { Cleanup = packet.Data; break; }
                    if (packet.Kind != "result" || packet.Result is not { } result || result.CommandId != packet.Id || !pending.TryRemove(packet.Id, out var completion))
                        throw new DriverException(Error.InvalidRequest);
                    completion.TrySetResult(result);
                }
            }
            finally
            {
                life.Cancel(); await pump;
                foreach (var request in pending.Values) request.TrySetException(new DriverException(Error.TransportLost));
                lifetime = null;
            }
        }
        private async Task Heartbeats(CancellationTokenSource life)
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(50, config.HeartbeatMilliseconds / 4)));
                do
                {
                    if (Cleanup != null || stopping) break;
                    var response = await Send(Build(JsonSerializer.SerializeToElement(new { name = "heartbeat" })), life.Token);
                    if ((response.ErrorCode != Error.None || !response.Verified) && !stopping) throw new DriverException(Error.TransportLost);
                } while (await timer.WaitForNextTickAsync(life.Token));
            }
            catch (Exception e) when (e is not OutOfMemoryException) { life.Cancel(); }
        }
        private Command Build(JsonElement arguments)
        {
            var node = JsonNode.Parse(arguments.GetRawText()) as JsonObject ?? throw new DriverException(Error.InvalidRequest);
            string[] reserved = ["protocolVersion", "runId", "machineId", "role", "commandId", "sequence", "targetSession", "expectedDesktop", "deadline", "manifestHash"];
            if (node.Any(p => reserved.Contains(p.Key, StringComparer.OrdinalIgnoreCase))) throw new DriverException(Error.InvalidRequest);
            var heartbeat = node["name"]?.GetValue<string>() == "heartbeat";
            node["protocolVersion"] = Wire.Version; node["runId"] = config.RunId; node["machineId"] = config.MachineId;
            node["role"] = config.Role; node["commandId"] = Guid.NewGuid().ToString("N");
            node["sequence"] = heartbeat ? Interlocked.Increment(ref heartbeatSequence) : Interlocked.Increment(ref sequence);
            node["targetSession"] = config.TargetSession; node["expectedDesktop"] = config.ExpectedDesktop;
            node["deadline"] = DateTimeOffset.UtcNow.AddSeconds(6) < config.ExpiresAt ? DateTimeOffset.UtcNow.AddSeconds(6) : config.ExpiresAt;
            node["manifestHash"] = hash;
            return Wire.Decode<Command>(node.ToJsonString());
        }
        public async Task<Result> Request(JsonElement arguments, CancellationToken cancellation)
        {
            var name = arguments.GetProperty("name").GetString();
            if (name == "heartbeat") throw new DriverException(Error.InvalidRequest);
            bool emergency = name == "stop";
            if (emergency) stopping = true;
            if (!emergency) await commands.WaitAsync(cancellation);
            try
            {
                if (DesktopActions.Handles(name)) return await DesktopActions.Execute(arguments, data => Send(Build(data), cancellation));
                return await Send(Build(arguments), cancellation);
            }
            finally { if (!emergency) commands.Release(); }
        }
        private async Task<Result> Send(Command command, CancellationToken cancellation)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            limit.CancelAfter(command.Name == "heartbeat" ? Math.Max(100, config.HeartbeatMilliseconds / 2) : 7000);
            var completion = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!pending.TryAdd(command.CommandId, completion)) throw new DriverException(Error.DuplicateMismatch);
            try
            {
                await channel.Write(new("command", command.CommandId, Command: command), limit.Token);
                return await completion.Task.WaitAsync(limit.Token);
            }
            catch { lifetime?.Cancel(); throw; }
            finally { pending.TryRemove(command.CommandId, out _); }
        }
    }
}

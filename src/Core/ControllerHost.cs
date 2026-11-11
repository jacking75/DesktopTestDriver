using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace DesktopTestDriver;

/// <summary>One command from a local client (CLI, json-lines, MCP, C# client) to the controller host.</summary>
public sealed record LocalCommand(string Command, string? MachineId = null, string? SessionId = null, string? RequestId = null, string? Request = null)
{
    public string Mode { get; init; } = "control";
    public bool Input { get; init; }
    public int IdleSeconds { get; init; } = 900;
    public TargetSpec? Targets { get; init; }
    public string? Launch { get; init; }
    public string[]? LaunchArguments { get; init; }
    public long? ExpectedRevision { get; init; }
    public string? Resolution { get; init; }
    public string? EvidenceRef { get; init; }
    public bool Resume { get; init; }
    public bool All { get; init; }
    public string? Output { get; init; }
}
public sealed record ConnectedAgent(AgentHello Hello, RpcPeer Peer, DateTimeOffset ConnectedAt);
/// <summary>Runs next to the coding agent. Accepts agents over plain TCP and local commands over a named pipe.</summary>
public sealed class ControllerHost
{
    /// <summary>The agent gets less time than the controller so its own timeout answers first.</summary>
    public const int AgentMarginMilliseconds = 150;
    private readonly ControllerSettings settings;
    private readonly StateStore store;
    private readonly IDisposable singleInstance;
    private readonly RequestJournal journal;
    private readonly ConcurrentDictionary<string, ConnectedAgent> agents = new();
    private readonly ConcurrentDictionary<string, SessionInfo> sessions = new();
    private readonly CancellationTokenSource stop = new();
    public int BoundPort { get; private set; }
    public ControllerHost(ControllerSettings settings)
    {
        this.settings = settings; store = new(settings.StateDirectory); singleInstance = store.Lock("controller.lock", 1);
        try
        {
            journal = new(store);
            journal.Recover(entry =>
            {
                var unknown = entry.Response with { Status = "completionUnknown", Error = new("ExecutionInterrupted", "ExecutionInterrupted") };
                if (entry.AgentResponse is not { Status: "completed" } agent) return unknown;
                if (entry.Operation != "capture") return agent;
                return entry.Output != null && entry.ExpectedHash != null && File.Exists(entry.Output) && new FileInfo(entry.Output).Length == entry.ExpectedBytes && GuiProtocol.Hash(File.ReadAllBytes(entry.Output)) == entry.ExpectedHash
                    ? agent with { Data = WithPath(agent.Data, entry.Output) } : unknown with { Status = "failed", Error = new("ImageDeliveryInterrupted", "ImageDeliveryInterrupted") };
            });
            foreach (var file in Directory.GetFiles(store.Root, "session-*.json"))
            {
                try { var info = store.Read<SessionInfo>(Path.GetFileName(file)); sessions[info.ControlSessionId] = info; } catch { }
            }
        }
        catch { singleInstance.Dispose(); throw; }
    }
    private static object WithPath(object? data, string path)
    {
        var map = GuiProtocol.Convert<Dictionary<string, JsonElement>>(data); map["path"] = JsonSerializer.SerializeToElement(path); return map;
    }
    private ConnectedAgent Agent(string machine) => agents.TryGetValue(machine, out var a) && a.Peer.Connected ? a : throw new GuiException("AgentDisconnected", "AgentDisconnected: " + machine);
    /// <summary>Without --machine the only connected agent is used.</summary>
    private ConnectedAgent Pick(string? machine)
    {
        if (machine != null) return Agent(machine);
        var connected = agents.Values.Where(a => a.Peer.Connected).ToArray();
        return connected.Length == 1 ? connected[0] : throw new GuiException(connected.Length == 0 ? "AgentDisconnected" : "MachineRequired", connected.Length == 0 ? "No agent is connected" : "Several agents are connected; pass --machine");
    }
    private SessionInfo Session(string id) => sessions.TryGetValue(id, out var s) ? s : throw new GuiException("SessionNotFound");
    private void Remember(SessionInfo info) { sessions[info.ControlSessionId] = info; store.Write("session-" + info.ControlSessionId + ".json", info, durable: false); }
    private void Remember(GuiResponse response)
    {
        if (response.Data == null) return;
        var data = GuiProtocol.Element(response.Data);
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("controlSessionId", out _) && data.TryGetProperty("mode", out _)) Remember(data.Deserialize<SessionInfo>(GuiProtocol.Json)!);
    }
    public async Task<GuiResponse> Dispatch(LocalCommand command)
    {
        var started = Stopwatch.GetTimestamp();
        var response = await DispatchCore(command);
        GuiRequest? request = null;
        if (command.Command == "request") { try { request = GuiProtocol.Parse(command.Request!); } catch { } }
        var sid = response.ControlSessionId ?? request?.ControlSessionId ?? command.SessionId;
        var machine = response.MachineId ?? command.MachineId;
        if (machine == null && sid != null && sessions.TryGetValue(sid, out var session)) machine = session.MachineId;
        return response with
        {
            Operation = response.Operation ?? request?.Operation ?? command.Command, MachineId = machine, ControlSessionId = sid,
            RequestId = response.RequestId ?? request?.RequestId ?? command.RequestId,
            ElapsedMilliseconds = command.Command == "request" && response.ElapsedMilliseconds > 0 ? Math.Max(response.ElapsedMilliseconds, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds) : (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds
        };
    }
    private async Task<GuiResponse> DispatchCore(LocalCommand command)
    {
        try
        {
            using var timeout = new CancellationTokenSource(10000);
            switch (command.Command)
            {
                case "status":
                    if (command.MachineId != null) return await Agent(command.MachineId).Peer.Call<GuiResponse>("status", new { }, timeout.Token);
                    await RefreshAll(timeout.Token);
                    return new("completed", Data: new
                    {
                        controller = new { listen = settings.ListenAddress + ":" + (BoundPort == 0 ? settings.Port : BoundPort), state = store.Root, wireVersion = GuiProtocol.WireVersion },
                        agents = agents.Values.Select(a => new
                        {
                            a.Hello.MachineId, connected = a.Peer.Connected, a.ConnectedAt,
                            sessions = sessions.Values.Where(s => s.MachineId == a.Hello.MachineId && s.State is not ("closed" or "expired")).Select(s => new { s.ControlSessionId, s.State, s.Mode, s.Input }).ToArray()
                        }).ToArray()
                    });
                case "doctor": return await Pick(command.MachineId).Peer.Call<GuiResponse>("doctor", new { }, timeout.Token);
                case "session-open": return await OpenSession(command);
                case "session-status":
                    if (command.SessionId != null)
                    {
                        var target = Session(command.SessionId);
                        try { Refresh(await Agent(target.MachineId).Peer.Call<GuiResponse>("sessions", new { all = true }, timeout.Token)); } catch (GuiException) { }
                        return new("completed", ControlSessionId: target.ControlSessionId, Data: Session(target.ControlSessionId));
                    }
                    await RefreshAll(timeout.Token, command.All);
                    return new("completed", Data: new { sessions = sessions.Values.Where(s => command.All || s.State is not ("closed" or "expired")).OrderBy(s => s.CreatedAt).ToArray() });
                case "session-close":
                {
                    var s = Session(command.SessionId!); using var closing = new CancellationTokenSource(15000);
                    var closed = await Agent(s.MachineId).Peer.Call<GuiResponse>("close", new { sessionId = s.ControlSessionId }, closing.Token); Remember(closed); return closed;
                }
                case "request-status":
                {
                    var entry = journal.Find(command.SessionId!, command.RequestId!);
                    if (entry == null) return GuiResponse.Reject("RequestNotFound", message: "Only mutations and captures are recorded; reads are never journaled");
                    if (entry.Response.Status == "completionUnknown") return await Reconcile(entry, timeout.Token);
                    return entry.Response;
                }
                case "request": return await Request(command);
                case "session-pause":
                case "session-resume":
                case "session-reconcile":
                {
                    var controlled = Session(command.SessionId!);
                    var result = await Agent(controlled.MachineId).Peer.Call<GuiResponse>(command.Command, new { sessionId = command.SessionId, expectedRevision = command.ExpectedRevision, requestId = command.RequestId, resolution = command.Resolution, evidenceRef = command.EvidenceRef, resume = command.Resume }, timeout.Token);
                    Remember(result); return result;
                }
                case "trace-export": return TraceExport(command.SessionId ?? throw new GuiException("SessionRequired"), command.Output ?? throw new GuiException("OutputRequired"));
                case "shutdown": stop.Cancel(); return new("completed", Data: new { stopping = true });
                default: return GuiResponse.Reject("InvalidCommand");
            }
        }
        catch (Exception e) { return GuiResponse.Reject(e is GuiException g ? g.Code : e is OperationCanceledException ? "RequestTimeout" : "InvalidRequest", data: (e as GuiException)?.Details, message: e.Message); }
    }
    private async Task<GuiResponse> OpenSession(LocalCommand command)
    {
        var agent = Pick(command.MachineId);
        using var timeout = new CancellationTokenSource(10000);
        var opened = await agent.Peer.Call<GuiResponse>("open", new OpenSession(command.Mode, command.Input, command.IdleSeconds, command.Targets), timeout.Token);
        Remember(opened);
        if (opened.Status != "completed" || command.Launch == null) return opened;
        // Open, launch and wait for the first window in one command.
        var request = new GuiRequest("launch-" + Guid.NewGuid().ToString("N")[..16], opened.ControlSessionId!, "launch", 30000,
            JsonSerializer.SerializeToElement(new { executable = Path.GetFullPath(command.Launch), arguments = command.LaunchArguments ?? [], lifetime = "session", waitWindow = new { timeoutMilliseconds = 20000 } }));
        var launched = await Request(new LocalCommand("request", Request: GuiProtocol.Encode(request)));
        if (launched.Status != "completed")
        {
            using var closing = new CancellationTokenSource(10000);
            try { Remember(await agent.Peer.Call<GuiResponse>("close", new { sessionId = opened.ControlSessionId }, closing.Token)); } catch { }
            return launched with { ControlSessionId = opened.ControlSessionId };
        }
        return opened with { Data = new { session = Session(opened.ControlSessionId!), launch = launched.Data } };
    }
    private async Task RefreshAll(CancellationToken cancellation, bool all = false)
    {
        foreach (var connected in agents.Values) { try { Refresh(await connected.Peer.Call<GuiResponse>("sessions", new { all }, cancellation)); } catch { } }
    }
    private void Refresh(GuiResponse response)
    {
        if (response.Status != "completed" || response.Data == null) return;
        foreach (var item in GuiProtocol.Element(response.Data).GetProperty("sessions").EnumerateArray()) Remember(item.Deserialize<SessionInfo>(GuiProtocol.Json)!);
    }
    private async Task<GuiResponse> Request(LocalCommand command)
    {
        var r = GuiProtocol.Parse(command.Request ?? throw new GuiException("InvalidRequest"));
        var session = Session(r.ControlSessionId); var d = Operations.Get(r.Operation);
        if (!d.Journaled)
        {
            // Reads are not journaled: no disk write, no duplicate check, and a timeout never blocks the session.
            var agent = Agent(session.MachineId);
            using var deadline = new CancellationTokenSource(r.TimeoutMilliseconds);
            try { return await agent.Peer.Call<GuiResponse>("execute", new AgentCommand(r, Math.Max(1, r.TimeoutMilliseconds - AgentMarginMilliseconds)), deadline.Token); }
            catch (OperationCanceledException) { return GuiResponse.Reject("RequestTimeout", r, message: "The agent did not answer in time; the session is unchanged") with { MachineId = session.MachineId }; }
            catch (GuiException e) { return GuiResponse.Reject(e.Code, r, message: e.Message) with { MachineId = session.MachineId }; }
        }
        var output = r.Operation == "capture" ? GuiProtocol.OptionalText(r.Arguments, "output") ?? Path.Combine(store.Root, "captures", r.ControlSessionId + "-" + r.RequestId + ".png") : null;
        Task<GuiResponse> processing;
        lock (journal.Gate(r.ControlSessionId, r.RequestId))
        {
            var existing = journal.Find(r.ControlSessionId, r.RequestId);
            if (existing != null) return existing.Fingerprint != GuiProtocol.Fingerprint(r) ? GuiResponse.Reject("DuplicateRequestMismatch", r) : existing.Response;
            var peer = Agent(session.MachineId);
            var accepted = journal.Accept(r, Deadline.Start(r.TimeoutMilliseconds), session.MachineId, output);
            processing = Task.Run(() => Forward(r, peer, accepted.Entry));
        }
        return await processing;
    }
    private async Task<GuiResponse> Reconcile(JournalEntry entry, CancellationToken cancellation)
    {
        try
        {
            var peer = Agent(entry.MachineId!);
            var original = await peer.Peer.Call<GuiResponse>("result", new { sessionId = entry.SessionId, requestId = entry.RequestId }, cancellation);
            if (original.RequestId != entry.RequestId || original.ControlSessionId != entry.SessionId) return entry.Response;
            if (original.Status is "completed" or "failed" or "rejected" && original.Error?.Code != "RequestNotFound")
            {
                if (entry.Operation == "capture" && original.Status == "completed")
                {
                    var capture = GuiProtocol.Convert<CaptureInfo>(original.Data);
                    original = entry.Output != null && File.Exists(entry.Output) && new FileInfo(entry.Output).Length == capture.Bytes && GuiProtocol.Hash(File.ReadAllBytes(entry.Output)) == capture.Sha256
                        ? original with { Data = WithPath(original.Data, entry.Output) } : original with { Status = "failed", Error = new("ImageDeliveryInterrupted", "ImageDeliveryInterrupted") };
                }
                lock (journal.Gate(entry.SessionId, entry.RequestId))
                {
                    var latest = journal.Find(entry.SessionId, entry.RequestId)!;
                    if (latest.Response.Status == "completionUnknown") journal.Save(latest with { AgentResponse = original, Response = original, OriginalUnknownResponse = latest.OriginalUnknownResponse ?? latest.Response });
                    return journal.Result(entry.SessionId, entry.RequestId);
                }
            }
        }
        catch { }
        return entry.Response;
    }
    private async Task<GuiResponse> Forward(GuiRequest r, ConnectedAgent agent, JournalEntry entry)
    {
        GuiResponse response; bool forwarded = false; GuiResponse? agentResponse = null;
        try
        {
            using var deadline = new CancellationTokenSource(Math.Max(1, entry.Deadline.Remaining));
            if (r.Operation == "capture" && File.Exists(entry.Output)) throw new GuiException("OutputExists") { NoEffect = true };
            if (entry.Deadline.Remaining <= AgentMarginMilliseconds) throw new GuiException("RequestTimeout") { NoEffect = true };
            forwarded = true;
            agentResponse = response = await agent.Peer.Call<GuiResponse>("execute", new AgentCommand(r, entry.Deadline.Remaining - AgentMarginMilliseconds), deadline.Token);
            if (r.Operation == "capture" && response.Status == "completed")
            {
                journal.Save(entry with { AgentResponse = response });
                response = await SaveImage(r, agent, entry, response, deadline.Token);
            }
        }
        catch (Exception e)
        {
            string code = e is GuiException g ? g.Code : e is OperationCanceledException ? "RequestTimeout" : "AgentDisconnected";
            var unknown = forwarded && agentResponse == null && !GuiProtocol.ReadOnly(r.Operation);
            response = new(agentResponse != null ? "failed" : unknown ? "completionUnknown" : "rejected", r.RequestId, r.ControlSessionId, Error: new(code, e.Message))
            { Operation = r.Operation, MachineId = entry.MachineId };
            if (unknown)
            {
                // The agent never answered: stop whatever is still running there and mark the session blocked.
                _ = Task.Run(async () =>
                {
                    try { using var stopTimeout = new CancellationTokenSource(5000); Remember(await agent.Peer.Call<GuiResponse>("cancel", new { sessionId = r.ControlSessionId }, stopTimeout.Token)); } catch { }
                });
                if (sessions.TryGetValue(r.ControlSessionId, out var s)) sessions[r.ControlSessionId] = s with { State = "blocked", PendingUnknownRequestIds = s.PendingUnknownRequestIds.Append(r.RequestId).Distinct().ToArray() };
            }
        }
        journal.Finish(r, response, agentResponse); return journal.Result(r.ControlSessionId, r.RequestId);
    }
    private async Task<GuiResponse> SaveImage(GuiRequest r, ConnectedAgent agent, JournalEntry entry, GuiResponse response, CancellationToken cancellation)
    {
        var capture = GuiProtocol.Convert<CaptureInfo>(response.Data);
        if (capture.Bytes is < 1 or > 67_108_864 || capture.WindowId != GuiProtocol.Text(r.Arguments, "windowId")) throw new GuiException("InvalidCapture");
        var path = entry.Output!; var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        var temp = Path.Combine(parent, ".desktop-driver-" + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
            {
                const int chunk = 1 << 20;
                for (long offset = 0; offset < capture.Bytes;)
                {
                    var data = await agent.Peer.Call<JsonElement>("chunk", new { captureId = capture.CaptureId, offset, size = chunk }, cancellation);
                    var bytes = Convert.FromBase64String(GuiProtocol.Text(data, "bytes"));
                    if (bytes.Length == 0 || bytes.Length > chunk || offset + bytes.Length > capture.Bytes) throw new GuiException("InvalidCaptureChunk");
                    await file.WriteAsync(bytes, cancellation); offset += bytes.Length;
                }
                await file.FlushAsync(cancellation);
            }
            if (new FileInfo(temp).Length != capture.Bytes || GuiProtocol.Hash(await File.ReadAllBytesAsync(temp, cancellation)) != capture.Sha256) throw new GuiException("ImageHashMismatch");
            journal.Save(entry with { AgentResponse = response, ExpectedHash = capture.Sha256, ExpectedBytes = capture.Bytes });
            File.Move(temp, path, false);
            // The image is safely here: the agent may delete its copy.
            _ = Task.Run(async () => { try { using var ack = new CancellationTokenSource(5000); await agent.Peer.Call<GuiResponse>("capture-ack", new { captureId = capture.CaptureId }, ack.Token); } catch { } });
            return response with { Data = WithPath(response.Data, path) };
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    /// <summary>Mutations of a session in time order, one JSON object per line, with their notes and capture paths.</summary>
    private GuiResponse TraceExport(string sessionId, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var entries = journal.Entries(sessionId).OrderBy(e => e.Deadline.AcceptedAt).ToArray();
        var path = Path.Combine(outputDirectory, "trace.jsonl");
        using (var writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(false)))
            foreach (var e in entries)
            {
                string? capturePath = e.Operation == "capture" && e.Response.Status == "completed" ? e.Output : null;
                writer.WriteLine(GuiProtocol.Encode(new { at = e.Deadline.AcceptedAt, e.RequestId, e.Operation, note = e.Note, arguments = e.Arguments, status = e.Response.Status, error = e.Response.Error, elapsedMilliseconds = e.Response.ElapsedMilliseconds, capturePath }));
            }
        return new("completed", ControlSessionId: sessionId, Data: new { path, entries = entries.Length });
    }
    private async Task AcceptAgent(TcpClient tcp, CancellationToken cancellation)
    {
        using (tcp)
        {
            tcp.NoDelay = true; tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            var stream = tcp.GetStream();
            using var opening = CancellationTokenSource.CreateLinkedTokenSource(cancellation); opening.CancelAfter(5000);
            var hello = await GuiWire.Read<AgentHello>(stream, opening.Token);
            if (hello.WireVersion != GuiProtocol.WireVersion) { await GuiWire.Write(stream, GuiResponse.Reject("IncompatiblePeerVersion", message: "controller wire " + GuiProtocol.WireVersion + ", agent wire " + hello.WireVersion), opening.Token); return; }
            if (!GuiProtocol.Id(hello.MachineId)) { await GuiWire.Write(stream, GuiResponse.Reject("InvalidMachineId"), opening.Token); return; }
            await using var peer = new RpcPeer(stream) { Handler = (method, _) => Task.FromResult<object>(method == "heartbeat" ? new GuiResponse("completed") : GuiResponse.Reject("InvalidCommand")) };
            var agent = new ConnectedAgent(hello, peer, DateTimeOffset.UtcNow);
            // A reconnect replaces a half-open old connection instead of being refused.
            agents.AddOrUpdate(hello.MachineId, agent, (_, old) => { old.Peer.Close(); return agent; });
            try
            {
                await GuiWire.Write(stream, new GuiResponse("completed"), opening.Token);
                var running = peer.Run(cancellation);
                while (!running.IsCompleted)
                {
                    await Task.WhenAny(running, Task.Delay(500, cancellation));
                    // The agent sends a heartbeat every 500 ms. Silence for 3 seconds means the connection is dead.
                    if (peer.SinceLastReceived > TimeSpan.FromSeconds(3)) { peer.Close(); break; }
                }
                try { await running; } catch { }
            }
            finally { agents.TryRemove(new KeyValuePair<string, ConnectedAgent>(hello.MachineId, agent)); }
        }
    }
    public async Task Run(CancellationToken cancellation)
    {
        using var once = singleInstance; using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stop.Token);
        store.Write("controller-process.json", Processes.Identify(Environment.ProcessId));
        var listener = new TcpListener(IPAddress.Parse(settings.ListenAddress), settings.Port); listener.Start();
        BoundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        Console.Error.WriteLine(GuiProtocol.Encode(new { code = "Listening", listen = settings.ListenAddress + ":" + BoundPort, state = store.Root }));
        var connections = new ConcurrentDictionary<long, Task>(); long nextConnection = 0;
        void Track(Task task)
        {
            var id = Interlocked.Increment(ref nextConnection); connections[id] = task;
            _ = task.ContinueWith(_ => connections.TryRemove(id, out var ignored), TaskScheduler.Default);
        }
        var network = Task.Run(async () =>
        {
            while (!linked.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync(linked.Token);
                Track(Task.Run(async () => { try { await AcceptAgent(tcp, linked.Token); } catch (Exception e) { tcp.Dispose(); if (!linked.IsCancellationRequested) Console.Error.WriteLine(GuiProtocol.Encode(new { code = e is GuiException g ? g.Code : "ConnectionRejected", detail = e.GetType().Name })); } }));
            }
        });
        var cleanup = Task.Run(async () =>
        {
            try { while (!linked.IsCancellationRequested) { await Task.Delay(60000, linked.Token); journal.Cleanup(); } } catch (OperationCanceledException) { }
        });
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(settings.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                try { await pipe.WaitForConnectionAsync(linked.Token); } catch { pipe.Dispose(); throw; }
                Track(Task.Run(async () =>
                {
                    using (pipe)
                    {
                        try
                        {
                            using var readTimeout = new CancellationTokenSource(3000); var command = await GuiWire.Read<LocalCommand>(pipe, readTimeout.Token);
                            var reply = await Dispatch(command);
                            using var writeTimeout = new CancellationTokenSource(3000); await GuiWire.Write(pipe, reply, writeTimeout.Token);
                        }
                        catch { /* Accepted requests continue even when their client disconnects. */ }
                    }
                }));
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var agent in agents.Values)
                foreach (var s in sessions.Values.Where(s => s.MachineId == agent.Hello.MachineId && s.State is not ("closed" or "expired")))
                { try { using var closing = new CancellationTokenSource(5000); Remember(await agent.Peer.Call<GuiResponse>("close", new { sessionId = s.ControlSessionId }, closing.Token)); } catch { } }
            linked.Cancel(); listener.Stop(); try { await network; } catch { } try { await cleanup; } catch { }
            try { await Task.WhenAll(connections.Values).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
    }
    /// <summary>Client side of the local pipe. Waits for the request timeout plus five seconds.</summary>
    public static async Task<GuiResponse> Call(ControllerSettings settings, LocalCommand command, CancellationToken cancellation = default)
    {
        GuiRequest? request = null;
        if (command.Command == "request" && command.Request != null) { try { request = GuiProtocol.Parse(command.Request); } catch { } }
        var limit = (request?.TimeoutMilliseconds ?? 10000) + 5000;
        if (command.Command == "session-open" && command.Launch != null) limit = 45000;
        if (command.Command == "session-close") limit = 20000;
        using var pipe = new NamedPipeClientStream(".", settings.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(limit);
        bool sent = false;
        try
        {
            await pipe.ConnectAsync(1000, deadline.Token);
            sent = true;
            await GuiWire.Write(pipe, command, deadline.Token);
            return await GuiWire.Read<GuiResponse>(pipe, deadline.Token);
        }
        catch
        {
            if (sent && request != null)
                return GuiProtocol.ReadOnly(request.Operation)
                    ? GuiResponse.Reject("ControllerResponseUnavailable", request)
                    : new("completionUnknown", request.RequestId, request.ControlSessionId, Error: new("ControllerResponseUnavailable", "Check the outcome with --request-status")) { Operation = request.Operation };
            return GuiResponse.Reject("ControllerUnavailable", message: "Start the controller with --serve (pipe " + settings.PipeName + ")");
        }
    }
}

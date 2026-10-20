using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace DesktopTestDriver;

public sealed record LocalCommand(string Command, string? MachineId = null, string? SessionId = null, string? RequestId = null,
    string? Request = null, string Mode = "control", int IdleSeconds = 900);
public sealed record ConnectedAgent(AgentHello Hello, string CertificateHash, RpcPeer Peer);
public sealed class ControllerHost
{
    private readonly ControllerSettings settings;
    private readonly StateStore store;
    private readonly IDisposable singleInstance;
    private readonly RequestJournal journal;
    private readonly ConcurrentDictionary<string, ConnectedAgent> agents = new();
    private readonly ConcurrentDictionary<string, SessionInfo> sessions = new();
    private readonly CancellationTokenSource stop = new();
    public ControllerHost(ControllerSettings settings)
    {
        this.settings = settings; store = new(settings.StateDirectory); singleInstance = store.Lock("controller.lock", 1);
        try
        {
            journal = new(store); journal.Recover(true);
            foreach (var file in Directory.GetFiles(store.Root, "session-*.json"))
            {
                var info = store.Read<SessionInfo>(Path.GetFileName(file));
                sessions[info.ControlSessionId] = info.State is "closed" or "expired" ? info : info with { State = "blocked", InputReleased = null, StopReason = "ControllerRestarted" };
            }
        }
        catch { singleInstance.Dispose(); throw; }
    }
    private ConnectedAgent Agent(string machine) => agents.TryGetValue(machine, out var a) && a.Peer.Connected ? a : throw new GuiException("AgentDisconnected");
    private SessionInfo Session(string id) => sessions.TryGetValue(id, out var s) ? s : throw new GuiException("SessionNotFound");
    private void Remember(GuiResponse response)
    {
        if (response.Data == null) return;
        var data = JsonSerializer.SerializeToElement(response.Data, GuiProtocol.Json);
        if (data.TryGetProperty("controlSessionId", out _))
        {
            var info = GuiProtocol.Decode<SessionInfo>(data.GetRawText()); store.Write("session-" + info.ControlSessionId + ".json", info); sessions[info.ControlSessionId] = info;
        }
    }
    public async Task<GuiResponse> Dispatch(LocalCommand command)
    {
        var response = await DispatchCore(command);
        if (response.Operation != null && response.MachineId != null) return response;
        GuiRequest? request = null;
        if (command.Command == "request") { try { request = GuiProtocol.Parse(command.Request!); } catch { } }
        var sid = response.ControlSessionId ?? request?.ControlSessionId ?? command.SessionId;
        var machine = response.MachineId ?? command.MachineId;
        if (machine == null && sid != null && sessions.TryGetValue(sid, out var session)) machine = session.MachineId;
        return response with
        {
            Operation = response.Operation ?? request?.Operation ?? command.Command,
            MachineId = machine,
            ControlSessionId = sid,
            RequestId = response.RequestId ?? request?.RequestId ?? command.RequestId
        };
    }
    private async Task<GuiResponse> DispatchCore(LocalCommand command)
    {
        try
        {
            using var timeout = new CancellationTokenSource(5000);
            switch (command.Command)
            {
                case "status":
                    if (command.MachineId != null) return await Agent(command.MachineId).Peer.Call<GuiResponse>("status", new { }, timeout.Token);
                    foreach (var connected in agents.Values) { try { Refresh(await connected.Peer.Call<GuiResponse>("sessions", new { }, timeout.Token)); } catch { } }
                    return new("completed", Data: new
                    {
                        agents = agents.Values.Select(a => new
                        {
                            a.Hello.MachineId,
                            connected = a.Peer.Connected,
                            a.Hello.InteractiveSessionId,
                            activeControlSessionId = sessions.Values.FirstOrDefault(s => s.MachineId == a.Hello.MachineId && s.Mode == "control" && s.State == "active")?.ControlSessionId
                        }).ToArray()
                    });
                case "session-open":
                    var a = Agent(command.MachineId ?? throw new GuiException("MachineRequired"));
                    var opened = await a.Peer.Call<GuiResponse>("open", new { mode = command.Mode, idleSeconds = command.IdleSeconds }, timeout.Token); Remember(opened); return opened;
                case "session-status":
                    if (command.SessionId != null)
                    {
                        var target = Session(command.SessionId);
                        var report = await Agent(target.MachineId).Peer.Call<GuiResponse>("sessions", new { }, timeout.Token);
                        Refresh(report); return new("completed", ControlSessionId: target.ControlSessionId, Data: Session(target.ControlSessionId));
                    }
                    foreach (var agent in agents.Values) { try { Refresh(await agent.Peer.Call<GuiResponse>("sessions", new { }, timeout.Token)); } catch { } }
                    return new("completed", Data: new { sessions = sessions.Values.ToArray() });
                case "session-close":
                    var s = Session(command.SessionId!); var closed = await Agent(s.MachineId).Peer.Call<GuiResponse>("close", new { sessionId = s.ControlSessionId }, timeout.Token); Remember(closed); return closed;
                case "request-status":
                    var entry = journal.Find(command.SessionId!, command.RequestId!);
                    if (entry == null) return GuiResponse.Reject("RequestNotFound");
                    if (entry.Response.Status == "completionUnknown") return await Reconcile(entry, timeout.Token);
                    return entry.Response;
                case "request":
                    var r = GuiProtocol.Parse(command.Request ?? throw new GuiException("InvalidRequest"));
                    var session = Session(r.ControlSessionId);
                    var fingerprint = GuiProtocol.Fingerprint(r);
                    Task<GuiResponse> processing;
                    lock (journal.Gate(r.ControlSessionId, r.RequestId))
                    {
                        var existing = journal.Find(r.ControlSessionId, r.RequestId);
                        if (existing != null)
                        {
                            if (existing.Fingerprint != fingerprint) return GuiResponse.Reject("DuplicateRequestMismatch", r);
                            return existing.Response;
                        }
                        var peer = Agent(session.MachineId);
                        var accepted = journal.Accept(r, Deadline.Start(r.TimeoutMilliseconds), session.MachineId, peer.CertificateHash);
                        processing = Task.Run(() => Forward(r, peer, accepted.Entry));
                    }
                    return await processing;
                case "shutdown": stop.Cancel(); return new("completed", Data: new { stopping = true });
                default: return GuiResponse.Reject("InvalidCommand");
            }
        }
        catch (Exception e) { return GuiResponse.Reject(e is GuiException g ? g.Code : e is OperationCanceledException ? "RequestTimeout" : "InvalidRequest"); }
    }
    private void Refresh(GuiResponse response)
    {
        if (response.Status != "completed" || response.Data == null) return;
        var data = JsonSerializer.SerializeToElement(response.Data, GuiProtocol.Json);
        foreach (var item in data.GetProperty("sessions").EnumerateArray()) Remember(new("completed", Data: item));
    }
    private async Task<GuiResponse> Reconcile(JournalEntry entry, CancellationToken cancellation)
    {
        try
        {
            var peer = Agent(entry.MachineId!);
            if (peer.CertificateHash != entry.CertificateHash) return entry.Response;
            var original = await peer.Peer.Call<GuiResponse>("result", new { sessionId = entry.SessionId, requestId = entry.RequestId }, cancellation);
            if (original.RequestId != entry.RequestId || original.ControlSessionId != entry.SessionId) return entry.Response;
            if (original.Status is "completed" or "failed" || original.Status == "rejected" && original.Error?.Code != "RequestNotFound")
            {
                if (entry.Operation == "capture" && original.Status == "completed")
                {
                    var capture = GuiProtocol.Decode<CaptureInfo>(GuiProtocol.Encode(original.Data));
                    if (entry.Output != null && File.Exists(entry.Output) && new FileInfo(entry.Output).Length == capture.Bytes && GuiProtocol.Hash(File.ReadAllBytes(entry.Output)) == capture.Sha256)
                    {
                        var data = GuiProtocol.Decode<Dictionary<string, object?>>(GuiProtocol.Encode(capture)); data["path"] = entry.Output;
                        original = original with { Data = data };
                    }
                    else original = original with { Status = "failed", Error = new("ImageDeliveryInterrupted", "ImageDeliveryInterrupted") };
                }
                lock (journal.Gate(entry.SessionId, entry.RequestId))
                {
                    var latest = journal.Find(entry.SessionId, entry.RequestId)!;
                    if (latest.Response.Status == "completionUnknown") journal.Save(latest with { AgentResponse = original, Response = original });
                    return journal.Result(entry.SessionId, entry.RequestId);
                }
            }
        }
        catch { }
        return entry.Response;
    }
    private async Task<GuiResponse> Forward(GuiRequest r, ConnectedAgent agent, JournalEntry entry)
    {
        GuiResponse response; bool forwarded = false;
        try
        {
            using var deadline = new CancellationTokenSource(Math.Max(1, entry.Deadline.Remaining));
            if (r.Operation == "capture" && File.Exists(entry.Output)) throw new GuiException("OutputExists");
            if (entry.Deadline.Remaining <= 0) throw new GuiException("RequestTimeout");
            forwarded = true;
            response = await agent.Peer.Call<GuiResponse>("execute", new AgentCommand(r, entry.Deadline.Remaining), deadline.Token);
            entry = entry with { AgentResponse = response }; journal.Save(entry);
            if (r.Operation == "capture" && response.Status == "completed") response = await SaveImage(r, agent, entry, response, deadline.Token);
        }
        catch (Exception e)
        {
            string code = e is GuiException g ? g.Code : e is OperationCanceledException ? "RequestTimeout" : "AgentDisconnected";
            response = new(entry.AgentResponse != null ? "failed" : forwarded ? "completionUnknown" : "rejected", r.RequestId, r.ControlSessionId,
                Error: new(code, code), InputEventsSent: entry.AgentResponse?.InputEventsSent ?? 0, InputReleased: entry.AgentResponse?.InputReleased)
            { Operation = r.Operation, MachineId = entry.MachineId };
            if (forwarded)
            {
                _ = Task.Run(async () =>
                {
                    try { using var stopTimeout = new CancellationTokenSource(3500); var stopped = await agent.Peer.Call<GuiResponse>("cancel", new { sessionId = r.ControlSessionId }, stopTimeout.Token); Remember(stopped); } catch { }
                });
                if (sessions.TryGetValue(r.ControlSessionId, out var s)) sessions[r.ControlSessionId] = s with { State = "blocked", InputReleased = null };
            }
        }
        journal.Finish(r, response); return journal.Result(r.ControlSessionId, r.RequestId);
    }
    private async Task<GuiResponse> SaveImage(GuiRequest r, ConnectedAgent agent, JournalEntry entry, GuiResponse response, CancellationToken cancellation)
    {
        var capture = GuiProtocol.Decode<CaptureInfo>(JsonSerializer.Serialize(response.Data, GuiProtocol.Json));
        if (capture.Bytes is < 1 or > 67_108_864 || capture.WindowId != GuiProtocol.Text(r.Arguments, "windowId")) throw new GuiException("InvalidCapture");
        var path = entry.Output!; var parent = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(parent)) throw new GuiException("OutputDirectoryNotFound");
        for (var d = new DirectoryInfo(parent); d != null; d = d.Parent) if ((d.Attributes & FileAttributes.ReparsePoint) != 0) throw new GuiException("UnsafeOutputPath");
        var temp = Path.Combine(parent, ".desktop-driver-" + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                for (long offset = 0; offset < capture.Bytes;)
                {
                    var data = await agent.Peer.Call<JsonElement>("chunk", new { captureId = capture.CaptureId, offset, size = 131072 }, cancellation);
                    var bytes = Convert.FromBase64String(GuiProtocol.Text(data, "bytes"));
                    if (bytes.Length == 0 || bytes.Length > 131072 || offset + bytes.Length > capture.Bytes) throw new GuiException("InvalidCaptureChunk");
                    await file.WriteAsync(bytes, cancellation); offset += bytes.Length;
                }
                await file.FlushAsync(cancellation); file.Flush(true);
            }
            cancellation.ThrowIfCancellationRequested();
            if (new FileInfo(temp).Length != capture.Bytes || GuiProtocol.Hash(await File.ReadAllBytesAsync(temp, cancellation)) != capture.Sha256) throw new GuiException("ImageHashMismatch");
            journal.Save(entry with { ExpectedHash = capture.Sha256, ExpectedBytes = capture.Bytes });
            cancellation.ThrowIfCancellationRequested(); File.Move(temp, path, false);
            var map = GuiProtocol.Decode<Dictionary<string, object?>>(GuiProtocol.Encode(capture)); map["path"] = path;
            var completed = response with { Data = map }; journal.Finish(r, completed); return completed;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private async Task AcceptAgent(TcpClient tcp, CancellationToken cancellation)
    {
        using (tcp)
        {
            using var certificate = ConnectionSettings.Certificate(store);
            var peers = store.Exists("peers.json") ? store.Read<PeerRegistration[]>("peers.json") : [];
            string? pin = null;
            using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) =>
            { if (cert == null) return false; pin = ConnectionSettings.Hash(cert); return peers.Any(p => p.CertificateHash == pin) && DateTime.UtcNow < cert.GetExpirationDateStringAsUtc(); });
            using var opening = CancellationTokenSource.CreateLinkedTokenSource(cancellation); opening.CancelAfter(5000);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate, ClientCertificateRequired = true, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, opening.Token);
            var hello = await GuiWire.Read<AgentHello>(ssl, opening.Token);
            if (!peers.Any(p => p.MachineId == hello.MachineId && p.CertificateHash == pin)) { await GuiWire.Write(ssl, GuiResponse.Reject("MachineCertificateMismatch"), opening.Token); return; }
            await using var peer = new RpcPeer(ssl) { Handler = (method, _) => Task.FromResult<object>(method == "heartbeat" ? new GuiResponse("completed") : GuiResponse.Reject("InvalidCommand")) };
            var agent = new ConnectedAgent(hello, pin!, peer);
            if (!agents.TryAdd(hello.MachineId, agent)) { await GuiWire.Write(ssl, GuiResponse.Reject("DuplicateMachineConnection"), opening.Token); return; }
            try { await GuiWire.Write(ssl, new GuiResponse("completed"), opening.Token); await peer.Run(cancellation); }
            finally { agents.TryRemove(new KeyValuePair<string, ConnectedAgent>(hello.MachineId, agent)); }
        }
    }
    public async Task Run(CancellationToken cancellation)
    {
        using var once = singleInstance; using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stop.Token);
        using var process = System.Diagnostics.Process.GetCurrentProcess(); store.Write("controller-process.json", Processes.Identify(process));
        var listener = new TcpListener(IPAddress.Parse(settings.ListenAddress), settings.Port); listener.Start();
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
                Track(Task.Run(async () => { try { await AcceptAgent(tcp, linked.Token); } catch (Exception e) { tcp.Dispose(); Console.Error.WriteLine(GuiProtocol.Encode(new { code = e is GuiException g ? g.Code : "ConnectionRejected", exceptionType = e.GetType().Name })); } }));
            }
        });
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(settings.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(linked.Token); } catch { pipe.Dispose(); throw; }
                Track(Task.Run(async () =>
                {
                    using (pipe)
                    {
                        try { using var readTimeout = new CancellationTokenSource(3000); var command = await GuiWire.Read<LocalCommand>(pipe, readTimeout.Token); var reply = await Dispatch(command); using var writeTimeout = new CancellationTokenSource(1000); await GuiWire.Write(pipe, reply, writeTimeout.Token); }
                        catch { /* Accepted requests continue even when their CLI disconnects. */ }
                    }
                }));
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var agent in agents.Values)
            {
                foreach (var s in sessions.Values.Where(s => s.MachineId == agent.Hello.MachineId && s.State is not ("closed" or "expired")))
                { try { using var closing = new CancellationTokenSource(3500); Remember(await agent.Peer.Call<GuiResponse>("close", new { sessionId = s.ControlSessionId }, closing.Token)); } catch { } }
            }
            linked.Cancel(); listener.Stop(); try { await network; } catch { }
            try { await Task.WhenAll(connections.Values).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
    }
    public static async Task<GuiResponse> Call(ControllerSettings settings, LocalCommand command)
    {
        using var pipe = new NamedPipeClientStream(".", settings.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(10000);
        try { await pipe.ConnectAsync(1000, deadline.Token); await GuiWire.Write(pipe, command, deadline.Token); return await GuiWire.Read<GuiResponse>(pipe, deadline.Token); }
        catch { return GuiResponse.Reject("ControllerUnavailable"); }
    }
}

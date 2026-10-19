using System.Collections.Concurrent;
using System.Text.Json;

namespace DesktopTestDriver;

internal sealed class ManagedAgent(AgentStatus initial, ControllerChannel channel)
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ControllerReply>> pending = new();
    public SemaphoreSlim Operations { get; } = new(1);
    public AgentStatus Status { get; private set; } = initial;
    public string? ActiveRun { get; set; } = initial.ActiveRun;
    public object Summary() => new { Status.MachineId, connected = true, Status.Agent, Status.Session, Status.Desktop, ActiveRun, Status.CleanupBlocked };
    public async Task Run(CancellationToken cancellation)
    {
        using var life = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var ping = Ping(life);
        try
        {
            while (!life.IsCancellationRequested)
            {
                var packet = await channel.Read(life.Token);
                if (packet.Kind != "agent-result" || packet.Data == null || !pending.TryRemove(packet.Id, out var completion))
                    throw new DriverException(Error.InvalidRequest);
                completion.TrySetResult(Wire.Decode<ControllerReply>(packet.Data.Value.GetRawText()));
            }
        }
        finally
        {
            life.Cancel();
            foreach (var item in pending.Values) item.TrySetException(new DriverException(Error.TransportLost));
            await ping;
        }
    }
    private async Task Ping(CancellationTokenSource life)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(750));
            do
            {
                var response = await Call("agent-ping", null, life.Token);
                if (!response.Ok) throw new DriverException(response.Error);
                var status = Wire.Decode<AgentStatus>(Wire.Encode(response.Data));
                if (status.MachineId != Status.MachineId || status.Agent != Status.Agent || status.Session != Status.Session || status.Desktop != "Default")
                    throw new DriverException(Error.SessionChanged);
                Status = status;
                if (Operations.CurrentCount == 1) ActiveRun = status.ActiveRun;
            } while (await timer.WaitForNextTickAsync(life.Token));
        }
        catch (Exception e) when (e is not OutOfMemoryException) { life.Cancel(); }
    }
    public async Task<ControllerReply> Call(string kind, JsonElement? data, CancellationToken cancellation)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation); limit.CancelAfter(kind == "agent-ping" ? 2500 : 8000);
        var id = Guid.NewGuid().ToString("N");
        var completed = new TaskCompletionSource<ControllerReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completed;
        try
        {
            await channel.Write(new(kind, id, Data: data), limit.Token);
            var reply = await completed.Task.WaitAsync(limit.Token);
            if (reply.Ok && kind is "agent-end" or "agent-shutdown")
            {
                var cleanup = Wire.Decode<AgentCleanup>(Wire.Encode(reply.Data));
                if (cleanup.Clean && cleanup.NormalDesktop) ActiveRun = null;
            }
            return reply;
        }
        finally { pending.TryRemove(id, out _); }
    }
}

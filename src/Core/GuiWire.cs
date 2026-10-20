using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace DesktopTestDriver;

public sealed record RpcMessage(string Id, string Method, JsonElement Payload, bool Reply = false);
public static class GuiWire
{
    public const int MaxFrameBytes = 2_097_152;
    public static async Task<T> Read<T>(Stream stream, CancellationToken cancellation)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, cancellation);
        var size = BinaryPrimitives.ReadInt32BigEndian(header);
        if (size is <= 0 or > MaxFrameBytes) throw new GuiException("MessageTooLarge");
        var bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, cancellation);
        return GuiProtocol.Decode<T>(Encoding.UTF8.GetString(bytes));
    }
    public static async Task Write<T>(Stream stream, T value, CancellationToken cancellation)
    {
        var bytes = Encoding.UTF8.GetBytes(GuiProtocol.Encode(value));
        if (bytes.Length > MaxFrameBytes) throw new GuiException("MessageTooLarge");
        var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellation); await stream.WriteAsync(bytes, cancellation); await stream.FlushAsync(cancellation);
    }
}
public sealed class RpcPeer(Stream stream) : IAsyncDisposable
{
    private readonly SemaphoreSlim sending = new(1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
    private readonly ConcurrentDictionary<long, Task> handlers = new();
    private readonly CancellationTokenSource stop = new();
    private long nextHandler;
    public Func<string, JsonElement, Task<object>>? Handler { get; set; }
    public bool Connected => !stop.IsCancellationRequested;
    public async Task<T> Call<T>(string method, object payload, CancellationToken cancellation)
    {
        var id = Guid.NewGuid().ToString("N"); var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Connected) throw new GuiException("AgentDisconnected");
        pending[id] = completion;
        try { await Send(new(id, method, JsonSerializer.SerializeToElement(payload, GuiProtocol.Json)), cancellation); return GuiProtocol.Decode<T>((await completion.Task.WaitAsync(cancellation)).GetRawText()); }
        finally { pending.TryRemove(id, out _); }
    }
    private async Task Send(RpcMessage message, CancellationToken cancellation)
    {
        await sending.WaitAsync(cancellation);
        try { await GuiWire.Write(stream, message, cancellation); } finally { sending.Release(); }
    }
    public async Task Run(CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stop.Token);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var message = await GuiWire.Read<RpcMessage>(stream, linked.Token);
                if (message.Reply) { if (pending.TryGetValue(message.Id, out var p)) p.TrySetResult(message.Payload); continue; }
                var key = Interlocked.Increment(ref nextHandler);
                var task = Task.Run(async () =>
                {
                    object result;
                    try { result = await (Handler ?? throw new GuiException("UnsupportedOperation"))(message.Method, message.Payload); }
                    catch (Exception e) { result = GuiResponse.Reject(e is GuiException g ? g.Code : "AgentUnavailable"); }
                    await Send(new(message.Id, message.Method, JsonSerializer.SerializeToElement(result, GuiProtocol.Json), true), linked.Token);
                }, linked.Token);
                handlers[key] = task;
                _ = task.ContinueWith(_ => handlers.TryRemove(key, out var ignored), TaskScheduler.Default);
            }
        }
        finally
        {
            stop.Cancel(); foreach (var p in pending.Values) p.TrySetException(new GuiException("AgentDisconnected"));
        }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); stream.Dispose();
        try { await Task.WhenAll(handlers.Values).WaitAsync(TimeSpan.FromSeconds(4)); } catch { }
    }
}

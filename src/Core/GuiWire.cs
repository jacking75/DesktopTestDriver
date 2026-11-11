using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DesktopTestDriver;

public sealed record RpcMessage(string Id, string Method, JsonElement Payload, bool Reply = false);
/// <summary>Length-prefixed UTF-8 JSON frames.</summary>
public static class GuiWire
{
    public const int MaxFrameBytes = 8 * 1024 * 1024;
    public static async Task<T> Read<T>(Stream stream, CancellationToken cancellation)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, cancellation);
        var size = BinaryPrimitives.ReadInt32BigEndian(header);
        if (size is <= 0 or > MaxFrameBytes) throw new GuiException("MessageTooLarge");
        var bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, cancellation);
        return GuiProtocol.Decode<T>(Encoding.UTF8.GetString(bytes));
    }
    /// <summary>Serializes and checks the size before anything is written, so an oversized value never corrupts the stream.</summary>
    public static byte[] Frame<T>(T value)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, GuiProtocol.Json);
        if (body.Length > MaxFrameBytes) throw new GuiException("ResponseTooLarge", "ResponseTooLarge: " + body.Length + " bytes");
        var frame = new byte[body.Length + 4]; BinaryPrimitives.WriteInt32BigEndian(frame, body.Length); body.CopyTo(frame, 4); return frame;
    }
    public static async Task Write<T>(Stream stream, T value, CancellationToken cancellation)
    {
        var frame = Frame(value);
        await stream.WriteAsync(frame, cancellation); await stream.FlushAsync(cancellation);
    }
}
/// <summary>Bidirectional request/reply over one stream.</summary>
public sealed class RpcPeer(Stream stream) : IAsyncDisposable
{
    private readonly SemaphoreSlim sending = new(1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
    private readonly ConcurrentDictionary<long, Task> handlers = new();
    private readonly CancellationTokenSource stop = new();
    private long nextHandler, lastReceived = Stopwatch.GetTimestamp();
    public Func<string, JsonElement, Task<object>>? Handler { get; set; }
    public bool Connected => !stop.IsCancellationRequested;
    public TimeSpan SinceLastReceived => Stopwatch.GetElapsedTime(Interlocked.Read(ref lastReceived));
    public async Task<T> Call<T>(string method, object payload, CancellationToken cancellation)
    {
        var id = Guid.NewGuid().ToString("N"); var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Connected) throw new GuiException("AgentDisconnected");
        var frame = GuiWire.Frame(new RpcMessage(id, method, JsonSerializer.SerializeToElement(payload, GuiProtocol.Json)));
        pending[id] = completion;
        try { await Send(frame, cancellation); return (await completion.Task.WaitAsync(cancellation)).Deserialize<T>(GuiProtocol.Json)!; }
        finally { pending.TryRemove(id, out _); }
    }
    private async Task Send(byte[] frame, CancellationToken cancellation)
    {
        await sending.WaitAsync(cancellation);
        try
        {
            if (!Connected) throw new GuiException("AgentDisconnected");
            await stream.WriteAsync(frame, cancellation); await stream.FlushAsync(cancellation);
        }
        catch
        {
            // A failed write may have sent only part of a frame. The connection cannot be reused.
            Close(); throw;
        }
        finally { sending.Release(); }
    }
    public void Close()
    {
        stop.Cancel(); try { stream.Dispose(); } catch { }
        foreach (var p in pending.Values) p.TrySetException(new GuiException("AgentDisconnected"));
    }
    public async Task Run(CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stop.Token);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var message = await GuiWire.Read<RpcMessage>(stream, linked.Token);
                Interlocked.Exchange(ref lastReceived, Stopwatch.GetTimestamp());
                if (message.Reply) { if (pending.TryGetValue(message.Id, out var p)) p.TrySetResult(message.Payload); continue; }
                var key = Interlocked.Increment(ref nextHandler);
                var task = Task.Run(async () =>
                {
                    object result;
                    try { result = await (Handler ?? throw new GuiException("UnsupportedOperation"))(message.Method, message.Payload); }
                    catch (Exception e) { result = e is GuiException g ? GuiResponse.Reject(g.Code, data: g.Details, message: g.Message) : GuiResponse.Reject("AgentUnavailable", message: e.GetType().Name + ": " + e.Message); }
                    byte[] frame;
                    try { frame = GuiWire.Frame(new RpcMessage(message.Id, message.Method, JsonSerializer.SerializeToElement(result, GuiProtocol.Json), true)); }
                    catch (GuiException e) when (e.Code == "ResponseTooLarge")
                    {
                        var request = result as GuiResponse;
                        var rejected = new GuiResponse("rejected", request?.RequestId, request?.ControlSessionId, Error: new("ResponseTooLarge", e.Message)) { Operation = request?.Operation, MachineId = request?.MachineId };
                        frame = GuiWire.Frame(new RpcMessage(message.Id, message.Method, JsonSerializer.SerializeToElement(rejected, GuiProtocol.Json), true));
                    }
                    await Send(frame, linked.Token);
                }, linked.Token);
                handlers[key] = task;
                _ = task.ContinueWith(_ => handlers.TryRemove(key, out var ignored), TaskScheduler.Default);
            }
        }
        finally { Close(); }
    }
    public async ValueTask DisposeAsync()
    {
        Close();
        try { await Task.WhenAll(handlers.Values).WaitAsync(TimeSpan.FromSeconds(4)); } catch { }
    }
}

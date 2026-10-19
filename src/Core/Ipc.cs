using System.IO.Pipes;
using System.Text;

namespace DesktopTestDriver;

public static class Lines
{
    public static async Task Drain(TextReader reader, CancellationToken cancellation)
    {
        var buffer = new char[4096]; int total = 0, count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation)) != 0)
            if ((total += count) > Wire.MaxMessage) throw new DriverException(Error.InvalidRequest);
    }
    public static async Task<string?> Read(TextReader reader, CancellationToken cancellation)
    {
        var buffer = new char[1];
        var line = new StringBuilder();
        while (await reader.ReadAsync(buffer.AsMemory(), cancellation) != 0)
        {
            if (buffer[0] == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length >= Wire.MaxMessage) throw new DriverException(Error.InvalidRequest);
            line.Append(buffer[0]);
        }
        return line.Length == 0 ? null : throw new DriverException(Error.InvalidRequest);
    }
}
public static class PipeRelay
{
    public static async Task<Result> Send(RunStore store, Command command, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(7));
        using var pipe = new NamedPipeClientStream(".", store.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        await writer.WriteLineAsync(Wire.Encode(new AuthRequest(File.ReadAllText(store.PathFor("secret")), command)).AsMemory(), timeout.Token);
        return Wire.Decode<Result>(await Lines.Read(reader, timeout.Token) ?? throw new DriverException(Error.TransportLost));
    }
    public static async Task Serve(RunStore store, CommandEngine engine, CancellationToken cancellation)
    {
        var handlers = new List<Task>();
        using var slots = new SemaphoreSlim(4);
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await slots.WaitAsync(cancellation);
                var pipe = new NamedPipeServerStream(store.PipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(cancellation); }
                catch { pipe.Dispose(); slots.Release(); throw; }
                handlers.RemoveAll(t => t.IsCompleted);
                handlers.Add(Handle(pipe));
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { await Task.WhenAll(handlers); }
        async Task Handle(NamedPipeServerStream pipe)
        {
            using (pipe)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(7000);
                try
                {
                    using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                    var request = Wire.Decode<AuthRequest>(await Lines.Read(reader, timeout.Token) ?? throw new DriverException(Error.InvalidRequest));
                    if (!store.Authenticate(request.Secret)) throw new DriverException(Error.AuthenticationFailed);
                    var result = await engine.Execute(request.Command);
                    await writer.WriteLineAsync(Wire.Encode(result).AsMemory(), timeout.Token);
                }
                catch (Exception e) when (e is not OutOfMemoryException) { /* Never return request bytes or secret values. */ }
                finally { slots.Release(); }
            }
        }
    }
}

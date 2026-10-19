using System.Net.Sockets;
using System.Text.Json;

namespace DesktopTestDriver;

public static class ClientController
{
    public static async Task Run(RunStore store, CommandEngine engine, ClientStatus status, ControllerConnection connection, CancellationToken cancellation)
    {
        ControllerFiles.Validate(connection, store);
        using var socket = new TcpClient { NoDelay = true };
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        startup.CancelAfter(Math.Max(100, store.Config.HeartbeatMilliseconds - 100));
        try
        {
            await socket.ConnectAsync(connection.Host, connection.Port, startup.Token);
            using var channel = new ControllerChannel(socket.GetStream());
            var hello = new ControllerPacket("hello", Hello: connection, Status: status);
            await channel.Write(hello, startup.Token);
            if ((await channel.Read(startup.Token)).Kind != "ready") throw new DriverException(Error.InvalidRequest);
            store.Write("controller-connected.json", new { connection.Host, connection.Port, at = DateTimeOffset.UtcNow });
            var requests = new List<Task>();
            using var operationStop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            try
            {
                while (!operationStop.IsCancellationRequested)
                {
                    var packet = await channel.Read(operationStop.Token);
                    if (packet.Kind != "command" || packet.Command is not { } command || packet.Id != command.CommandId) throw new DriverException(Error.InvalidRequest);
                    requests.RemoveAll(t => t.IsCompleted);
                    // Controller has at most one normal command, one heartbeat and one emergency stop in flight.
                    if (requests.Count >= 4) throw new DriverException(Error.InvalidRequest);
                    requests.Add(Respond(command));
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException) { store.Stop(Error.TransportLost); }
            finally
            {
                store.Stop(Error.TransportLost);
                await Task.WhenAll(requests);
                // Main Client loop performs cleanup and cancels this task. A disconnect independently triggers that loop.
                if (File.Exists(store.PathFor("cleanup.json")))
                {
                    using var finish = new CancellationTokenSource(1000);
                    try { await channel.Write(new("cleanup", Data: store.Read<JsonElement>("cleanup.json")), finish.Token); }
                    catch (Exception e) when (e is not OutOfMemoryException) { }
                }
            }
            async Task Respond(Command command)
            {
                try
                {
                    var result = await engine.Execute(command);
                    // Drain accepted results even when cleanup has cancelled the receive loop.
                    using var reply = new CancellationTokenSource(1000);
                    await channel.Write(new("result", command.CommandId, Result: result), reply.Token);
                }
                catch (Exception e) when (e is not OutOfMemoryException) { store.Stop(Error.TransportLost); operationStop.Cancel(); }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            store.Write("controller-failure.json", new { error = e is DriverException d ? d.Code.ToString() : e.GetType().Name, at = DateTimeOffset.UtcNow });
            store.Stop(Error.TransportLost);
        }
    }
}

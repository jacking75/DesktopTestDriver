using System.Text;
using System.Text.Json;

namespace DesktopTestDriver;

public sealed record ControllerSettings(string Host, int Port, string PipeName);
public sealed record ControllerEnrollment(RunConfig Config, string ManifestHash);
public sealed record ControllerConnection(int TransportVersion, string Host, int Port,
    string RunId, string MachineId, string ManifestHash);
public sealed record ControllerPacket(string Kind, string Id = "", ControllerConnection? Hello = null,
    ClientStatus? Status = null, Command? Command = null, Result? Result = null, JsonElement? Data = null);
public sealed record ControllerRequest(string Operation, string? RunId = null, JsonElement? Arguments = null);
public sealed record ControllerReply(bool Ok, object? Data = null, Error Error = Error.None);

public static class ControllerFiles
{
    public static ControllerSettings Settings(string root)
    {
        RunStore.CheckPrivate(root);
        return Wire.Decode<ControllerSettings>(File.ReadAllText(Path.Combine(root, "controller.json")));
    }
    public static ControllerSettings Initialize(string root, string host, int port)
    {
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || port is < 25001 or > 25199) throw new DriverException(Error.InvalidRequest);
        RunStore.CreatePrivateDirectory(Path.GetFullPath(root));
        var settings = new ControllerSettings(host, port, "DTD-Controller-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(Path.Combine(root, "controller.json"), Wire.Encode(settings));
        return settings;
    }
    public static ControllerConnection Enroll(string root, string configPath)
    {
        var settings = Settings(root);
        var config = Wire.Decode<RunConfig>(File.ReadAllText(configPath));
        config.Validate(); Authorization.ValidateDevelopment(config);
        if (config.ExpiresAt <= DateTimeOffset.UtcNow) throw new DriverException(Error.Expired);
        var hash = Wire.FileHash(configPath);
        var enrollment = new ControllerEnrollment(config, hash);
        using var file = new FileStream(Path.Combine(root, config.RunId + ".enrollment.json"), FileMode.CreateNew);
        file.Write(Encoding.UTF8.GetBytes(Wire.Encode(enrollment)));
        return new(1, settings.Host, settings.Port, config.RunId, config.MachineId, hash);
    }
    public static void Validate(ControllerConnection connection, RunStore store)
    {
        if (connection.TransportVersion != 1 || connection.Port is < 25001 or > 25199 ||
            Uri.CheckHostName(connection.Host) == UriHostNameType.Unknown ||
            connection.RunId != store.Config.RunId ||
            connection.MachineId != store.Config.MachineId || connection.ManifestHash != store.ManifestHash)
            throw new DriverException(Error.DelegationMismatch);
    }
}

// One reader and serialized writers per connection; byte limits apply before JSON parsing.
public sealed class ControllerChannel(Stream stream) : IDisposable
{
    private readonly StreamReader reader = new(stream, new UTF8Encoding(false, true), false, 4096, true);
    private readonly StreamWriter writer = new(stream, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
    private readonly SemaphoreSlim writes = new(1);
    public async Task<ControllerPacket> Read(CancellationToken cancellation)
    {
        var line = await Lines.Read(reader, cancellation) ?? throw new DriverException(Error.TransportLost);
        if (Encoding.UTF8.GetByteCount(line) > Wire.MaxMessage) throw new DriverException(Error.InvalidRequest);
        return Wire.Decode<ControllerPacket>(line);
    }
    public async Task Write(ControllerPacket packet, CancellationToken cancellation)
    {
        await writes.WaitAsync(cancellation);
        try
        {
            var value = Wire.Encode(packet);
            if (Encoding.UTF8.GetByteCount(value) > Wire.MaxMessage) throw new DriverException(Error.InvalidRequest);
            await writer.WriteLineAsync(value.AsMemory(), cancellation);
        }
        finally { writes.Release(); }
    }
    public void Dispose() { reader.Dispose(); writer.Dispose(); writes.Dispose(); }
}

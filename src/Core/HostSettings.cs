using System.Globalization;
using System.Net;

namespace DesktopTestDriver;

public sealed record ControllerSettings(string StateDirectory, string ListenAddress = "127.0.0.1", int Port = HostSettings.DefaultPort)
{
    public string PipeName => "DesktopTestDriver-" + GuiProtocol.Hash(Path.GetFullPath(StateDirectory).ToUpperInvariant())[..24];
}
public sealed record AgentSettings(string StateDirectory, string MachineId, string ControllerHost = "127.0.0.1", int ControllerPort = HostSettings.DefaultPort,
    int InteractiveSessionId = 0, int MaxHoldMilliseconds = 1500, int MaxStopMilliseconds = 3000);
/// <summary>Command-line first. A JSON settings file is optional; arguments override it.</summary>
public static class HostSettings
{
    public const int DefaultPort = 25001;
    public static string DefaultState(string role) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopTestDriver", role);
    public static string DefaultMachine() { var name = new string(Environment.MachineName.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray()).ToLowerInvariant(); return name.Length == 0 ? "local" : name[..Math.Min(80, name.Length)]; }
    /// <summary>Removes the options this parser understands from <paramref name="args"/>.</summary>
    private static Dictionary<string, string> Take(List<string> args, params string[] names)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Count;)
        {
            if (names.Contains(args[i]) && i + 1 < args.Count) { if (!found.TryAdd(args[i], args[i + 1])) throw new GuiException("InvalidCommand", "DuplicateOption: " + args[i]); args.RemoveRange(i, 2); }
            else i++;
        }
        return found;
    }
    private static (string Host, int Port) Endpoint(string value)
    {
        var colon = value.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(value.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535) throw new GuiException("InvalidConfiguration", "Endpoint must be HOST:PORT");
        return (value[..colon], port);
    }
    public static ControllerSettings Controller(List<string> args)
    {
        var o = Take(args, "--listen", "--state", "--config");
        var config = o.GetValueOrDefault("--config") ?? Path.Combine(AppContext.BaseDirectory, "Controller.json");
        var s = File.Exists(config) ? GuiProtocol.Decode<ControllerSettings>(File.ReadAllText(config)) : new ControllerSettings(DefaultState("Controller"));
        if (o.TryGetValue("--state", out var state)) s = s with { StateDirectory = Path.GetFullPath(state) };
        if (o.TryGetValue("--listen", out var listen)) { var (host, port) = Endpoint(listen); s = s with { ListenAddress = host, Port = port }; }
        if (!IPAddress.TryParse(s.ListenAddress, out _) || !Path.IsPathFullyQualified(s.StateDirectory)) throw new GuiException("InvalidConfiguration");
        return s;
    }
    public static AgentSettings Agent(List<string> args)
    {
        var o = Take(args, "--controller", "--machine", "--state", "--config", "--session", "--max-hold-ms", "--max-stop-ms");
        var config = o.GetValueOrDefault("--config") ?? Path.Combine(AppContext.BaseDirectory, "Agent.json");
        var s = File.Exists(config) ? GuiProtocol.Decode<AgentSettings>(File.ReadAllText(config)) : new AgentSettings(DefaultState("Agent"), DefaultMachine());
        if (o.TryGetValue("--state", out var state)) s = s with { StateDirectory = Path.GetFullPath(state) };
        if (o.TryGetValue("--machine", out var machine)) s = s with { MachineId = machine };
        if (o.TryGetValue("--controller", out var controller)) { var (host, port) = Endpoint(controller); s = s with { ControllerHost = host, ControllerPort = port }; }
        int Int(string name, int fallback) => o.TryGetValue(name, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;
        s = s with { InteractiveSessionId = Int("--session", s.InteractiveSessionId), MaxHoldMilliseconds = Int("--max-hold-ms", s.MaxHoldMilliseconds), MaxStopMilliseconds = Int("--max-stop-ms", s.MaxStopMilliseconds) };
        if (!GuiProtocol.Id(s.MachineId) || !Path.IsPathFullyQualified(s.StateDirectory) || string.IsNullOrWhiteSpace(s.ControllerHost) || s.InteractiveSessionId < 0 || s.MaxHoldMilliseconds is < 100 or > 60000 || s.MaxStopMilliseconds is < 100 or > 10000) throw new GuiException("InvalidConfiguration");
        return s;
    }
}

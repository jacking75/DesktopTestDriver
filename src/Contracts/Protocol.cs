using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;

namespace DesktopTestDriver;

public static class Wire
{
    public const int Version = 2;
    public const int MaxMessage = 1_048_576;
    public static void ConfigureConsole()
    {
        Console.InputEncoding = new UTF8Encoding(false, true);
        Console.OutputEncoding = new UTF8Encoding(false);
    }
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T Decode<T>(string value)
    {
        if (value.Length > MaxMessage) throw new DriverException(Error.InvalidRequest);
        // System.Text.Json otherwise accepts duplicate properties with last-write-wins.
        using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 32 });
        Unique(document.RootElement);
        return JsonSerializer.Deserialize<T>(value, Json) ?? throw new DriverException(Error.InvalidRequest);
    }
    private static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in element.EnumerateObject())
            {
                if (!names.Add(p.Name)) throw new DriverException(Error.InvalidRequest);
                Unique(p.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) Unique(item);
    }
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string FileHash(string path) => Hash(File.ReadAllBytes(path));
    public static bool Id(string value) => value.Length is >= 1 and <= 80 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

public enum Error
{
    None, InvalidRequest, Expired, DelegationMismatch, ReservationConflict, SessionChanged,
    DesktopChanged, StaleFrame, WindowChanged, ForegroundMismatch, InjectionFailed,
    ObservationUnavailable, CleanupIncomplete, SequenceRejected, DuplicateMismatch,
    Stopped, WatchdogUnavailable, UserInputConflict, Unsupported, RecoveryRequired, TransportLost,
    BarrierFailed, AuthenticationFailed
}
public sealed class DriverException(Error code) : Exception(code.ToString())
{
    public Error Code { get; } = code;
}
public enum RecoveryGrade { R0, R1, R2 }
public sealed record Rect(int X, int Y, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= X && y >= Y && x < (long)X + Width && y < (long)Y + Height;
}
public sealed record WindowIdentity(long Hwnd, int Pid, DateTimeOffset CreatedAt, string ImagePath, string ImageHash, string Title, Rect Bounds);
public sealed record MonitorInfo(string Id, Rect Bounds, uint DpiX, uint DpiY, bool Primary);
public sealed record UiNode(string Name, string AutomationId, string ControlType, Rect Bounds, bool Enabled, string? Toggle);
public sealed record Observation(int Session, string Desktop, long Foreground, WindowIdentity[] Windows, MonitorInfo[] Monitors, bool Elevated);
public sealed record Frame(string Id, long CapturedTick, DateTimeOffset ObservedAt, Observation Observation, WindowIdentity Window, string? ImagePath, string? ImageHash, UiNode[] Controls);
public sealed record WindowScope(string ImagePath, string ImageHash, string Title);
public sealed record EvidencePolicy(int RetainHours = 24, long MaxBytes = 67_108_864, bool Images = false);
public sealed record Delegation(string Owner, string Requester, string KeyId, string[] Scenarios, string[] Actions,
    string[] ProductPermissions, EvidencePolicy Evidence, string Signature)
{
    public Dictionary<string, string> ScenarioHashes { get; init; } = [];
}
public sealed record OwnerKey(string Owner, string KeyId, string PublicKey);
public sealed record ArtifactScope(string Id, string Path, int MaxBytes, string Kind);
public sealed record FixtureScope(string ImagePath, string ImageHash, int MaxSeconds = 300);
public sealed record ApplicationScope(string Id, string ImagePath, string ImageHash, string[] Arguments);
public sealed record RunConfig
{
    public int ProtocolVersion { get; init; } = Wire.Version;
    public required string RunId { get; init; }
    public required string MachineId { get; init; }
    public required string Role { get; init; }
    public required int TargetSession { get; init; }
    public string ExpectedDesktop { get; init; } = "Default";
    public required DateTimeOffset ExpiresAt { get; init; }
    public int MaxSeconds { get; init; } = 600;
    public int HeartbeatMilliseconds { get; init; } = 3000;
    public int MaxHoldMilliseconds { get; init; } = 1500;
    public int FrameMaxAgeMilliseconds { get; init; } = 1500;
    public RecoveryGrade Recovery { get; init; }
    public string? RecoveryEvidence { get; init; }
    public bool AllowInput { get; init; }
    public WindowScope[] Windows { get; init; } = [];
    public int[] AllowedKeys { get; init; } = [];
    public Delegation? Delegation { get; init; }
    public ArtifactScope[] ObservationFiles { get; init; } = [];
    public FixtureScope? Fixture { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ApplicationScope[]? Applications { get; init; }
    public void Validate()
    {
        if (ProtocolVersion != Wire.Version || !Enum.IsDefined(Recovery) || !Wire.Id(RunId) || !Wire.Id(MachineId) || !Wire.Id(Role) || TargetSession <= 0 || ExpectedDesktop != "Default" ||
            MaxSeconds is < 1 or > 900 || HeartbeatMilliseconds is < 200 or > 5000 || MaxHoldMilliseconds is < 50 or > 5000 ||
            FrameMaxAgeMilliseconds is < 50 or > 5000 || Windows.Length > 32 || AllowedKeys.Length > 256 || AllowedKeys.Any(k => k is < 1 or > 254))
            throw new DriverException(Error.InvalidRequest);
        if (AllowInput && (Recovery < RecoveryGrade.R1 || string.IsNullOrWhiteSpace(RecoveryEvidence) || Windows.Length == 0))
            throw new DriverException(Error.RecoveryRequired);
        if (Windows.Any(w => !Path.IsPathFullyQualified(w.ImagePath) || !IsHash(w.ImageHash) || w.Title.Length is < 1 or > 512))
            throw new DriverException(Error.InvalidRequest);
        if (Applications is { } apps && (apps.Length > 32 || apps.Select(a => a.Id).Distinct().Count() != apps.Length ||
            apps.Any(a => !Wire.Id(a.Id) || !Path.IsPathFullyQualified(a.ImagePath) || a.ImagePath.StartsWith("\\\\", StringComparison.Ordinal) ||
                !IsHash(a.ImageHash) || a.Arguments.Length > 32 || a.Arguments.Any(s => s.Length > 4096 || s.Contains('\0')) ||
                !Windows.Any(w => w.ImagePath.Equals(a.ImagePath, StringComparison.OrdinalIgnoreCase) && w.ImageHash == a.ImageHash))))
            throw new DriverException(Error.InvalidRequest);
        if (Fixture is { } fixture && (!Path.IsPathFullyQualified(fixture.ImagePath) || !IsHash(fixture.ImageHash) || fixture.MaxSeconds is < 10 or > 300 ||
            !Windows.Any(w => w.ImagePath.Equals(fixture.ImagePath, StringComparison.OrdinalIgnoreCase) && w.ImageHash == fixture.ImageHash && w.Title == "DesktopTestDriver marker " + RunId)))
            throw new DriverException(Error.DelegationMismatch);
        if (ObservationFiles.Length > 16 || ObservationFiles.Select(f => f.Id).Distinct().Count() != ObservationFiles.Length ||
            ObservationFiles.Any(f => !Wire.Id(f.Id) || !Path.IsPathFullyQualified(f.Path) || f.Path.StartsWith("\\\\", StringComparison.Ordinal) ||
                f.MaxBytes < 1 || f.MaxBytes > (f.Kind == "file" ? 67_108_864 : 524288) || f.Kind is not ("json" or "ndjson" or "file")))
            throw new DriverException(Error.InvalidRequest);
    }
    public static bool IsHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
}
public sealed record Command
{
    public int ProtocolVersion { get; init; } = Wire.Version;
    public required string RunId { get; init; }
    public required string MachineId { get; init; }
    public required string Role { get; init; }
    public required string CommandId { get; init; }
    public required long Sequence { get; init; }
    public required int TargetSession { get; init; }
    public required string ExpectedDesktop { get; init; }
    public required DateTimeOffset Deadline { get; init; }
    public required string ManifestHash { get; init; }
    public required string Name { get; init; }
    public WindowIdentity? ExpectedWindow { get; init; }
    public string? FrameId { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int EndX { get; init; }
    public int EndY { get; init; }
    public int Delta { get; init; }
    public string Button { get; init; } = "left";
    public int[] Keys { get; init; } = [];
    public string? Text { get; init; }
    public int TimeoutMilliseconds { get; init; } = 1000;
    public int Steps { get; init; } = 10;
    public string? Condition { get; init; }
    public string? ApplicationId { get; init; }
}
public sealed record Result(string CommandId, bool Accepted, bool Executed, bool Verified, Error ErrorCode, DateTimeOffset ObservedAt, double DurationMilliseconds, object? Evidence);
public sealed record AuthRequest(string Secret, Command Command);
public sealed record ProcessIdentity(int Pid, DateTimeOffset CreatedAt, string ImagePath, string ImageHash);
public sealed record OwnedProcess(ProcessIdentity Identity);
public sealed record HeldInput(string Kind, int Code, long SinceTick);
public sealed record InputLedger(HeldInput[] Held, OwnedProcess[] Processes);
public sealed record ClientStatus(ProcessIdentity Client, ProcessIdentity Watchdog, int Session, string Desktop, string ManifestHash, string PipeName);

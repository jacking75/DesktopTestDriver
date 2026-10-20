using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopTestDriver;

public sealed class GuiException(string code, string? detail = null) : Exception(detail ?? code) { public string Code { get; } = code; }
public sealed record GuiRequest(int ApiVersion, string RequestId, string ControlSessionId, string Operation,
    int TimeoutMilliseconds, JsonElement Arguments);
public sealed record GuiError(string Code, string Message);
public sealed record GuiResponse(string Status, string? RequestId = null, string? ControlSessionId = null,
    object? Data = null, GuiError? Error = null, int InputEventsSent = 0, bool? InputReleased = null)
{
    public int ApiVersion => 1;
    public string? Operation { get; init; }
    public string? MachineId { get; init; }
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
    [JsonIgnore]
    public int ExitCode => Status switch { "completed" => 0, "completionUnknown" => 2, "running" => 3, _ => 1 };
    public static GuiResponse Reject(string code, GuiRequest? r = null) => new("rejected", r?.RequestId, r?.ControlSessionId, Error: new(code, code)) { Operation = r?.Operation };
}
public static class GuiProtocol
{
    public const int Version = 1, MaxRequestBytes = 1_048_576, MaxCommandMilliseconds = 5000;
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };
    public static readonly string[] Operations = ["windows", "controls", "capture", "focus", "click", "type-text", "key", "chord", "move", "wheel", "drag", "launch", "close-window", "terminate-owned", "release-input"];
    public static bool ReadOnly(string operation) => operation is "windows" or "controls" or "capture";
    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T Decode<T>(string value)
    {
        using var document = JsonDocument.Parse(value); Unique(document.RootElement);
        return JsonSerializer.Deserialize<T>(value, Json) ?? throw new GuiException("InvalidRequest");
    }
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject()) { if (!names.Add(property.Name)) throw new GuiException("DuplicateField"); Unique(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Unique(item);
    }
    public static bool Id(string? value) => value is { Length: > 0 and <= 80 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    public static GuiRequest Parse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxRequestBytes) throw new GuiException("RequestTooLarge");
        using var document = JsonDocument.Parse(json); Unique(document.RootElement);
        var root = document.RootElement;
        Fields(root, "apiVersion requestId controlSessionId operation timeoutMilliseconds arguments");
        var version = root.GetProperty("apiVersion").GetInt32();
        if (version != Version) throw new GuiException("UnsupportedApiVersion");
        var operation = Text(root, "operation");
        if (!Operations.Contains(operation)) throw new GuiException("UnsupportedOperation");
        var id = Text(root, "requestId"); var session = Text(root, "controlSessionId");
        if (!Id(id) || !Id(session)) throw new GuiException("InvalidRequest");
        var timeout = root.TryGetProperty("timeoutMilliseconds", out var t) ? t.GetInt32() : 5000;
        if (timeout is < 1 or > 5000) throw new GuiException("InvalidTimeout");
        var a = root.GetProperty("arguments");
        Fields(a, operation switch
        {
            "windows" or "release-input" => "",
            "controls" or "focus" or "close-window" => "windowId",
            "capture" => "windowId output",
            "click" => "windowId elementId automationId position button",
            "type-text" => "windowId text",
            "key" => "windowId key action",
            "chord" => "windowId keys",
            "move" => "windowId position",
            "wheel" => "windowId position delta",
            "drag" => "windowId captureId start end button steps",
            "launch" => "executable arguments lifetime",
            "terminate-owned" => "processIds",
            _ => ""
        });
        if (operation is not ("windows" or "release-input" or "launch" or "terminate-owned")) _ = Text(a, "windowId");
        switch (operation)
        {
            case "capture": if (!Path.IsPathFullyQualified(Text(a, "output"))) throw new GuiException("InvalidOutputPath"); break;
            case "click":
                if (new[] { "elementId", "automationId", "position" }.Count(n => a.TryGetProperty(n, out _)) != 1) throw new GuiException("AmbiguousTarget");
                if (a.TryGetProperty("position", out var pos)) Position(pos, true);
                else _ = Text(a, a.TryGetProperty("elementId", out _) ? "elementId" : "automationId");
                Button(a); break;
            case "type-text":
                var text = Text(a, "text");
                if (text.Length > 4096 || text.Any(char.IsControl) || !ValidUnicode(text)) throw new GuiException("InvalidText"); break;
            case "key": _ = Key(Text(a, "key")); if (Text(a, "action") is not ("press" or "down" or "up")) throw new GuiException("InvalidAction"); break;
            case "chord":
                var keys = a.GetProperty("keys").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
                if (keys.Length is < 1 or > 8 || keys.Distinct().Count() != keys.Length) throw new GuiException("InvalidKeys");
                foreach (var key in keys) _ = Key(key); break;
            case "move":
            case "wheel":
                Position(a.GetProperty("position"), true);
                if (operation == "wheel" && a.GetProperty("delta").GetInt32() is < -1200 or > 1200) throw new GuiException("InvalidDelta"); break;
            case "drag":
                _ = Text(a, "captureId"); Position(a.GetProperty("start"), false); Position(a.GetProperty("end"), false); Button(a);
                if (a.TryGetProperty("steps", out var steps) && steps.GetInt32() is < 1 or > 100) throw new GuiException("InvalidSteps"); break;
            case "launch":
                if (!Path.IsPathFullyQualified(Text(a, "executable"))) throw new GuiException("InvalidExecutable");
                foreach (var arg in a.GetProperty("arguments").EnumerateArray()) if (arg.ValueKind != JsonValueKind.String || arg.GetString()!.Contains('\0')) throw new GuiException("InvalidArguments");
                if (a.TryGetProperty("lifetime", out var life) && life.GetString() is not ("keep" or "session")) throw new GuiException("InvalidLifetime"); break;
            case "terminate-owned":
                var ids = a.GetProperty("processIds").EnumerateArray().ToArray(); if (ids.Length is < 1 or > 128) throw new GuiException("InvalidProcesses");
                foreach (var value in ids) if (!Id(value.GetString())) throw new GuiException("InvalidProcesses"); break;
        }
        var normalized = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(a.GetRawText())!;
        if (operation is "click" or "drag" && !normalized.ContainsKey("button")) normalized["button"] = JsonSerializer.SerializeToElement("left");
        if (operation == "drag" && !normalized.ContainsKey("steps")) normalized["steps"] = JsonSerializer.SerializeToElement(10);
        if (operation == "launch" && !normalized.ContainsKey("lifetime")) normalized["lifetime"] = JsonSerializer.SerializeToElement("keep");
        return new(version, id, session, operation, timeout, JsonSerializer.SerializeToElement(normalized));
    }
    private static bool ValidUnicode(string value)
    {
        for (int i = 0; i < value.Length; i++) if (char.IsSurrogate(value[i]) && (!char.IsHighSurrogate(value[i]) || ++i >= value.Length || !char.IsLowSurrogate(value[i]))) return false;
        return true;
    }
    public static void Fields(JsonElement e, string allowed)
    {
        if (e.ValueKind != JsonValueKind.Object || e.EnumerateObject().Any(p => !allowed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(p.Name))) throw new GuiException("UnknownField");
    }
    public static string Text(JsonElement a, string name) => a.GetProperty(name).GetString() is { Length: > 0 } s ? s : throw new GuiException("InvalidRequest");
    private static void Position(JsonElement p, bool capture) { Fields(p, capture ? "x y captureId" : "x y"); _ = p.GetProperty("x").GetInt32(); _ = p.GetProperty("y").GetInt32(); if (capture) _ = Text(p, "captureId"); }
    public static int Button(JsonElement a) => !a.TryGetProperty("button", out var b) ? 1 : b.GetString() switch { "left" => 1, "right" => 2, "middle" => 3, _ => throw new GuiException("InvalidButton") };
    public static int Key(string key)
    {
        if (key.Length == 1 && (key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')) return key[0];
        if (key.StartsWith('F') && int.TryParse(key.AsSpan(1), out var f) && f is >= 1 and <= 24) return 0x70 + f - 1;
        return key switch { "CTRL" => 0x11, "ALT" => 0x12, "SHIFT" => 0x10, "WIN" => 0x5B, "ENTER" => 13, "TAB" => 9, "ESC" => 27, "BACKSPACE" => 8, "DELETE" => 46, "SPACE" => 32, "LEFT" => 37, "RIGHT" => 39, "UP" => 38, "DOWN" => 40, "HOME" => 36, "END" => 35, "PAGEUP" => 33, "PAGEDOWN" => 34, _ => throw new GuiException("InvalidKey") };
    }
    public static string Fingerprint(GuiRequest r)
    {
        using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream)) Canonical(JsonSerializer.SerializeToElement(r, Json), writer);
        return Hash(stream.ToArray());
    }
    private static void Canonical(JsonElement e, Utf8JsonWriter writer)
    {
        if (e.ValueKind == JsonValueKind.Object) { writer.WriteStartObject(); foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) { writer.WritePropertyName(p.Name); Canonical(p.Value, writer); } writer.WriteEndObject(); }
        else if (e.ValueKind == JsonValueKind.Array) { writer.WriteStartArray(); foreach (var v in e.EnumerateArray()) Canonical(v, writer); writer.WriteEndArray(); }
        else e.WriteTo(writer);
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DesktopTestDriver;

/// <summary>Driver error. <see cref="NoEffect"/> marks a refusal raised before any dispatch began.</summary>
public sealed class GuiException(string code, string? detail = null, object? details = null) : Exception(detail ?? code)
{
    public string Code { get; } = code;
    public object? Details { get; } = details;
    public bool NoEffect { get; init; }
}
public sealed record GuiRequest(string RequestId, string ControlSessionId, string Operation, int TimeoutMilliseconds, JsonElement Arguments, string? Note = null);
public sealed record GuiError(string Code, string Message, object? Data = null);
public sealed record GuiResponse(string Status, string? RequestId = null, string? ControlSessionId = null, object? Data = null, GuiError? Error = null)
{
    public string? Operation { get; init; }
    public string? MachineId { get; init; }
    public long ElapsedMilliseconds { get; init; }
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
    [JsonIgnore]
    public int ExitCode => Status switch { "completed" => 0, "completionUnknown" => 2, "running" => 3, _ => 1 };
    public static GuiResponse Reject(string code, GuiRequest? r = null, object? data = null, string? message = null) =>
        new("rejected", r?.RequestId, r?.ControlSessionId, Error: new(code, message ?? code, data)) { Operation = r?.Operation };
}
public static class GuiProtocol
{
    public const int WireVersion = 3, MaxRequestBytes = 1_048_576, MaxCommandMilliseconds = 5000, MaxWaitMilliseconds = 30000, DefaultTimeout = 5000;
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Korean text stays readable UTF-8 instead of \uXXXX escapes: fewer bytes and tokens.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public static bool ReadOnly(string operation) => Operations.Get(operation).ReadOnly;
    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T Decode<T>(string value)
    {
        using var document = JsonDocument.Parse(value); Unique(document.RootElement);
        return document.RootElement.Deserialize<T>(Json) ?? throw new GuiException("InvalidRequest");
    }
    public static T Convert<T>(object? value) => value is JsonElement e ? e.Deserialize<T>(Json)! : JsonSerializer.SerializeToElement(value, Json).Deserialize<T>(Json)!;
    public static JsonElement Element(object? value) => value is JsonElement e ? e : JsonSerializer.SerializeToElement(value, Json);
    public static string Hash(byte[] bytes) => System.Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));
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
        json = json.TrimStart('﻿'); // PowerShell pipes may prepend a byte order mark.
        if (Encoding.UTF8.GetByteCount(json) > MaxRequestBytes) throw new GuiException("RequestTooLarge");
        using var document = JsonDocument.Parse(json); Unique(document.RootElement);
        var root = document.RootElement;
        Fields(root, "requestId controlSessionId operation timeoutMilliseconds arguments note");
        var operation = Text(root, "operation");
        var id = Text(root, "requestId"); var session = Text(root, "controlSessionId");
        if (!Id(id) || !Id(session)) throw new GuiException("InvalidRequest");
        var timeout = root.TryGetProperty("timeoutMilliseconds", out var t) ? t.GetInt32() : DefaultTimeout;
        string? note = root.TryGetProperty("note", out var n) && n.ValueKind != JsonValueKind.Null ? n.GetString() : null;
        if (note is { Length: > 120 }) throw new GuiException("InvalidNote");
        var arguments = root.TryGetProperty("arguments", out var a) ? a.Clone() : JsonSerializer.SerializeToElement(new { });
        var request = new GuiRequest(id, session, operation, timeout, arguments, note);
        Validate(request); return request;
    }
    /// <summary>Validates one request. Called at the controller boundary and again at the agent without re-encoding.</summary>
    public static void Validate(GuiRequest r)
    {
        var d = Operations.Find(r.Operation) ?? throw new GuiException("UnsupportedOperation", "Unknown operation '" + r.Operation + "'. Operations: " + string.Join(", ", Operations.All.Select(o => o.Operation)));
        if (!Id(r.RequestId) || !Id(r.ControlSessionId)) throw new GuiException("InvalidRequest");
        if (r.TimeoutMilliseconds < 1 || r.TimeoutMilliseconds > d.MaxTimeout) throw new GuiException("InvalidTimeout");
        if (r.Note is { Length: > 120 }) throw new GuiException("InvalidNote");
        ArgumentValidator.Validate(r.Operation, r.Arguments);
    }
    public static void Fields(JsonElement e, string allowed)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new GuiException("UnknownField");
        var names = allowed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in e.EnumerateObject()) if (!names.Contains(p.Name)) throw new GuiException("UnknownField", "Unknown field '" + p.Name + "' (allowed: " + (names.Length == 0 ? "none" : string.Join(", ", names)) + ")");
    }
    public static string Text(JsonElement a, string name) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : throw new GuiException("InvalidRequest", "Field '" + name + "' must be a non-empty string");
    public static string? OptionalText(JsonElement a, string name) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;
    public static int? OptionalInt(JsonElement a, string name) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetInt32() : null;
    public static bool Flag(JsonElement a, string name, bool fallback = false) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetBoolean() : fallback;
    public static bool Has(JsonElement a, string name) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;
    public static int Button(JsonElement a) => !Has(a, "button") ? 1 : a.GetProperty("button").GetString() switch { "left" => 1, "right" => 2, "middle" => 3, _ => throw new GuiException("InvalidButton") };
    private static readonly Dictionary<string, int> NamedKeys = new(StringComparer.Ordinal)
    {
        ["CTRL"] = 0x11, ["ALT"] = 0x12, ["SHIFT"] = 0x10, ["WIN"] = 0x5B, ["ENTER"] = 13, ["TAB"] = 9, ["ESC"] = 27, ["BACKSPACE"] = 8,
        ["DELETE"] = 46, ["INSERT"] = 45, ["SPACE"] = 32, ["LEFT"] = 37, ["RIGHT"] = 39, ["UP"] = 38, ["DOWN"] = 40, ["HOME"] = 36,
        ["END"] = 35, ["PAGEUP"] = 33, ["PAGEDOWN"] = 34, ["APPS"] = 0x5D, ["PRINTSCREEN"] = 0x2C,
        [";"] = 0xBA, ["="] = 0xBB, [","] = 0xBC, ["-"] = 0xBD, ["."] = 0xBE, ["/"] = 0xBF, ["`"] = 0xC0, ["["] = 0xDB, ["\\"] = 0xDC, ["]"] = 0xDD, ["'"] = 0xDE
    };
    public static int Key(string key)
    {
        if (key.Length == 1 && (key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')) return key[0];
        if (key.StartsWith('F') && int.TryParse(key.AsSpan(1), out var f) && f is >= 1 and <= 24 && key == "F" + f.ToString(System.Globalization.CultureInfo.InvariantCulture)) return 0x70 + f - 1;
        if (key.StartsWith("NUMPAD", StringComparison.Ordinal) && key.Length == 7 && key[6] is >= '0' and <= '9') return 0x60 + key[6] - '0';
        return NamedKeys.TryGetValue(key, out var code) ? code : throw new GuiException("InvalidKey");
    }
    public static bool ValidUnicode(string value)
    {
        for (int i = 0; i < value.Length; i++) if (char.IsSurrogate(value[i]) && (!char.IsHighSurrogate(value[i]) || ++i >= value.Length || !char.IsLowSurrogate(value[i]))) return false;
        return true;
    }
    public static string Fingerprint(GuiRequest r)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Canonical(JsonSerializer.SerializeToElement(r with { Note = null }, Json), writer);
        return Hash(stream.ToArray());
    }
    private static void Canonical(JsonElement e, Utf8JsonWriter writer)
    {
        if (e.ValueKind == JsonValueKind.Object) { writer.WriteStartObject(); foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) { writer.WritePropertyName(p.Name); Canonical(p.Value, writer); } writer.WriteEndObject(); }
        else if (e.ValueKind == JsonValueKind.Array) { writer.WriteStartArray(); foreach (var v in e.EnumerateArray()) Canonical(v, writer); writer.WriteEndArray(); }
        else e.WriteTo(writer);
    }
    public static Regex SafeRegex(string pattern)
    {
        if (pattern.Length is < 1 or > 256) throw new GuiException("InvalidSelector", "RegexTooLong");
        try { return new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant); }
        catch (ArgumentException) { throw new GuiException("InvalidSelector", "InvalidRegex"); }
    }
}

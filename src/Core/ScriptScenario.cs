using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DesktopTestDriver;

// Public, application-independent file contract. No product adapter is loaded by this path.
public sealed record ScriptCondition(string Path, string Op, JsonElement Value);
public sealed record ScriptSelection(string Path, ScriptCondition[] Where);
public sealed record ScriptStep(string Id, string Role, JsonElement Command, ScriptSelection? Select = null,
    ScriptCondition[]? Expect = null, bool WaitFor = false, int TimeoutMilliseconds = 5000, int PollMilliseconds = 100);
public sealed record ScriptScenario(int SchemaVersion, string Id, string Purpose, string[] Roles, string[] Parameters,
    int MaximumSeconds, ScriptStep[] Steps)
{
    public string Hash() => Wire.Hash(Encoding.UTF8.GetBytes(Wire.Encode(this)));
    [System.Text.Json.Serialization.JsonIgnore]
    public int CleanupSeconds => Roles.Length * 8 + 2;
    public void Validate()
    {
        if (SchemaVersion != 1 || !Wire.Id(Id) || string.IsNullOrWhiteSpace(Purpose) || Purpose.Length > 4096 ||
            Roles is not { Length: >= 1 and <= 8 } || Roles.Any(r => !Wire.Id(r)) || Roles.Distinct().Count() != Roles.Length ||
            Parameters == null || Parameters.Length > 128 || Parameters.Any(p => !Wire.Id(p)) || Parameters.Distinct().Count() != Parameters.Length ||
            MaximumSeconds is < 20 or > 900 || MaximumSeconds <= CleanupSeconds || Steps is not { Length: >= 1 and <= 128 } ||
            !Steps.Any(s => s.Expect is { Length: > 0 })) throw new DriverException(Error.InvalidRequest);
        var previous = new HashSet<string>();
        foreach (var step in Steps)
        {
            if (!Wire.Id(step.Id) || previous.Contains(step.Id) || !Roles.Contains(step.Role) ||
                step.TimeoutMilliseconds is < 1 or > 5000 || step.PollMilliseconds is < 100 or > 1000)
                throw new DriverException(Error.InvalidRequest);
            ScriptCommands.Validate(step.Command);
            var name = step.Command.GetProperty("name").GetString()!;
            if (step.WaitFor && (name is not ("inspect" or "controls" or "read-artifact") || step.Select == null && step.Expect is not { Length: > 0 }))
                throw new DriverException(Error.InvalidRequest);
            if (step.Select is { } select)
            {
                ScriptValues.Tokens(select.Path);
                if (select.Where is not { Length: > 0 and <= 32 }) throw new DriverException(Error.InvalidRequest);
                foreach (var condition in select.Where) ScriptValues.Validate(condition);
            }
            if (step.Expect is { } conditions)
            {
                if (conditions.Length > 32) throw new DriverException(Error.InvalidRequest);
                foreach (var condition in conditions) ScriptValues.Validate(condition);
            }
            ScriptValues.ValidateReferences(JsonSerializer.SerializeToElement(step, Wire.Json), previous, Parameters);
            previous.Add(step.Id);
        }
    }
}

public sealed record ScriptBindings(Dictionary<string, JsonElement> Parameters, Dictionary<string, string> Roles);
public sealed record ScriptExecution(ScriptScenario Plan, Dictionary<string, JsonElement> Parameters, Dictionary<string, RunConfig> Runs)
{
    public static ScriptExecution Load(string scenarioPath, string bindingsPath)
    {
        var plan = Wire.Decode<ScriptScenario>(File.ReadAllText(scenarioPath));
        var bindings = Wire.Decode<ScriptBindings>(File.ReadAllText(bindingsPath));
        var directory = Path.GetDirectoryName(Path.GetFullPath(bindingsPath))!;
        var runs = bindings.Roles.ToDictionary(p => p.Key,
            p => Wire.Decode<RunConfig>(File.ReadAllText(Path.GetFullPath(p.Value, directory))));
        var execution = new ScriptExecution(plan, bindings.Parameters, runs);
        execution.Validate();
        return execution;
    }
    public void Validate()
    {
        Plan.Validate();
        if (Parameters == null || Runs == null || !Parameters.Keys.ToHashSet().SetEquals(Plan.Parameters) ||
            !Runs.Keys.ToHashSet().SetEquals(Plan.Roles) || Runs.Values.Select(r => r.MachineId).Distinct().Count() != Runs.Count ||
            Runs.Values.Select(r => r.RunId).Distinct().Count() != Runs.Count) throw new DriverException(Error.InvalidRequest);
        foreach (var (role, config) in Runs)
        {
            config.Validate(); Authorization.ValidateDevelopment(config);
            if (config.Role != role || config.MaxSeconds < Plan.MaximumSeconds) throw new DriverException(Error.DelegationMismatch);
        }
        foreach (var step in Plan.Steps)
        {
            // Resolve all parameter paths before beginning any work, including unused branches of templates.
            ScriptValues.CheckParameters(JsonSerializer.SerializeToElement(step, Wire.Json), Parameters);
            var partial = Wire.Decode<ScriptStep>(ScriptValues.Resolve(JsonSerializer.SerializeToElement(step, Wire.Json),
                JsonSerializer.SerializeToElement(new { parameters = Parameters }, Wire.Json), parametersOnly: true).GetRawText());
            ScriptCommands.Validate(partial.Command);
            foreach (var condition in (partial.Expect ?? []).Concat(partial.Select?.Where ?? [])) ScriptValues.Validate(condition);
            var name = step.Command.GetProperty("name").GetString()!;
            var config = Runs[step.Role];
            if (name == "launch" && partial.Command.GetProperty("applicationId") is { ValueKind: JsonValueKind.String } app &&
                config.Applications?.Any(a => a.Id == app.GetString()) != true) throw new DriverException(Error.DelegationMismatch);
            if (name == "read-artifact" && partial.Command.GetProperty("condition") is { ValueKind: JsonValueKind.String } artifact &&
                !config.ObservationFiles.Any(f => f.Id == artifact.GetString())) throw new DriverException(Error.DelegationMismatch);
            foreach (var action in ScriptCommands.Required(name)) Authorization.Require(config, action);
            if (ScriptCommands.Mutates(name) && (!config.AllowInput || config.Recovery < RecoveryGrade.R1 || string.IsNullOrWhiteSpace(config.RecoveryEvidence)))
                throw new DriverException(Error.RecoveryRequired);
        }
        if (Encoding.UTF8.GetByteCount(Wire.Encode(this)) > Wire.MaxMessage - 1024) throw new DriverException(Error.InvalidRequest);
    }
}

public static class ScriptCommands
{
    private static readonly Dictionary<string, string[]> Fields = new()
    {
        ["launch"] = ["applicationId"], ["inspect"] = [], ["controls"] = ["expectedWindow"],
        ["focus"] = ["expectedWindow", "frameId"], ["snapshot"] = ["expectedWindow", "frameId"],
        ["type-window"] = ["hwnd", "title", "text"], ["click-control"] = ["hwnd", "title", "automationId"],
        ["move-window"] = ["hwnd", "title", "x", "y"], ["click-window"] = ["hwnd", "title", "x", "y"],
        ["move"] = ["expectedWindow", "frameId", "x", "y"], ["click"] = ["expectedWindow", "frameId", "x", "y", "button"],
        ["wheel"] = ["expectedWindow", "frameId", "x", "y", "delta"],
        ["drag"] = ["expectedWindow", "frameId", "x", "y", "endX", "endY", "steps", "button"],
        ["key-down"] = ["expectedWindow", "frameId", "keys"], ["key-up"] = ["expectedWindow", "frameId", "keys"],
        ["chord"] = ["expectedWindow", "frameId", "keys"], ["text"] = ["expectedWindow", "frameId", "text"],
        ["read-artifact"] = ["condition"], ["release-all"] = []
    };
    public static void Validate(JsonElement command, bool resolved = false)
    {
        if (command.ValueKind != JsonValueKind.Object || !command.TryGetProperty("name", out var name) ||
            name.ValueKind != JsonValueKind.String || !Fields.TryGetValue(name.GetString()!, out var fields) ||
            command.EnumerateObject().Any(p => p.Name != "name" && !(p.Name == "timeoutMilliseconds" && !DesktopActions.Handles(name.GetString())) && !fields.Contains(p.Name)))
            throw new DriverException(Error.InvalidRequest);
        var commandName = name.GetString()!;
        foreach (var field in fields.Where(f => f is not ("button" or "steps" or "hwnd" or "title")))
            if (!command.TryGetProperty(field, out _)) throw new DriverException(Error.InvalidRequest);
        if (DesktopActions.Handles(commandName) && !command.TryGetProperty("hwnd", out _) && !command.TryGetProperty("title", out _))
            throw new DriverException(Error.InvalidRequest);
        foreach (var property in command.EnumerateObject())
        {
            var value = property.Value;
            if (!resolved && value.ValueKind == JsonValueKind.Object && value.TryGetProperty("$ref", out _)) continue;
            bool valid = property.Name switch
            {
                "name" => true,
                "expectedWindow" => Window(value, resolved),
                "hwnd" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var hwnd) && hwnd > 0,
                "x" or "y" or "endX" or "endY" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var coordinate) && coordinate >= 0,
                "delta" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var delta) && Math.Abs((long)delta) <= 1200,
                "steps" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) && count is >= 1 and <= 100,
                "timeoutMilliseconds" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var timeout) && timeout is >= 1 and <= 5000,
                "keys" => Keys(value, resolved, commandName),
                "button" => value.ValueKind == JsonValueKind.String && value.GetString() is "left" or "right" or "middle",
                "text" => value.ValueKind == JsonValueKind.String && value.GetString() is { Length: >= 1 and <= 256 } text && !text.Any(char.IsControl),
                _ => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) && value.GetString()!.Length <= 512
            };
            if (!valid) throw new DriverException(Error.InvalidRequest);
        }
    }
    private static bool Keys(JsonElement value, bool resolved, string command)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > 8 || command != "chord" && value.GetArrayLength() != 1) return false;
        var seen = new HashSet<int>();
        foreach (var item in value.EnumerateArray())
        {
            if (!resolved && item.ValueKind == JsonValueKind.Object && item.TryGetProperty("$ref", out _)) continue;
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var key) || key is < 1 or > 254 || !seen.Add(key)) return false;
        }
        return true;
    }
    private static bool Window(JsonElement value, bool resolved)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        static bool References(JsonElement item) => item.ValueKind switch
        {
            JsonValueKind.Object => item.TryGetProperty("$ref", out _) || item.EnumerateObject().Any(p => References(p.Value)),
            JsonValueKind.Array => item.EnumerateArray().Any(References), _ => false
        };
        if (!resolved && References(value)) return true;
        var window = Wire.Decode<WindowIdentity>(value.GetRawText());
        return window.Hwnd > 0 && window.Pid > 0 && window.CreatedAt != default && !string.IsNullOrWhiteSpace(window.ImagePath) &&
            window.ImageHash != null && RunConfig.IsHash(window.ImageHash) && window.Title != null && window.Bounds is { Width: > 0, Height: > 0 };
    }
    public static string[] Required(string name) => name switch
    {
        "type-window" => ["inspect", "focus", "text"], "click-control" => ["inspect", "focus", "controls", "click"],
        "move-window" => ["inspect", "focus", "move"], "click-window" => ["inspect", "focus", "click"], _ => [name]
    };
    public static bool Mutates(string name) => name is not ("inspect" or "controls" or "snapshot" or "read-artifact" or "release-all");
}

public static class ScriptValues
{
    public static string[] Tokens(string pointer)
    {
        if (pointer == "") return [];
        if (pointer == null || pointer.Length > 1024 || !pointer.StartsWith('/')) throw new DriverException(Error.InvalidRequest);
        var tokens = pointer[1..].Split('/');
        foreach (var token in tokens)
            for (int i = 0; i < token.Length; i++)
                if (token[i] == '~' && (++i >= token.Length || token[i] is not ('0' or '1'))) throw new DriverException(Error.InvalidRequest);
        return tokens.Select(t => t.Replace("~1", "/").Replace("~0", "~")).ToArray();
    }
    public static bool TryPointer(JsonElement root, string pointer, out JsonElement value)
    {
        value = root;
        foreach (var token in Tokens(pointer))
        {
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(token, out var child)) value = child;
            else if (value.ValueKind == JsonValueKind.Array && token.Length > 0 && (token.Length == 1 || token[0] != '0') &&
                token.All(char.IsAsciiDigit) && int.TryParse(token, out var index) && index < value.GetArrayLength()) value = value[index];
            else { value = default; return false; }
        }
        return true;
    }
    private static bool Reference(JsonElement value, out string pointer)
    {
        pointer = "";
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("$ref", out var reference)) return false;
        if (value.EnumerateObject().Count() != 1 || reference.ValueKind != JsonValueKind.String) throw new DriverException(Error.InvalidRequest);
        pointer = reference.GetString()!; return true;
    }
    private static void References(JsonElement value, Action<string> visit)
    {
        if (Reference(value, out var pointer)) { visit(pointer); return; }
        if (value.ValueKind == JsonValueKind.Object) foreach (var property in value.EnumerateObject()) References(property.Value, visit);
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) References(item, visit);
    }
    public static void ValidateReferences(JsonElement value, HashSet<string> previous, string[] parameters) => References(value, pointer =>
    {
        var tokens = Tokens(pointer);
        if (tokens.Length < 2 || !(tokens[0] == "parameters" && parameters.Contains(tokens[1]) || tokens[0] == "steps" && previous.Contains(tokens[1])))
            throw new DriverException(Error.InvalidRequest);
    });
    public static void CheckParameters(JsonElement value, Dictionary<string, JsonElement> parameters)
    {
        var root = JsonSerializer.SerializeToElement(new { parameters }, Wire.Json);
        References(value, pointer => { if (Tokens(pointer)[0] == "parameters" && !TryPointer(root, pointer, out _)) throw new DriverException(Error.InvalidRequest); });
    }
    public static JsonElement Resolve(JsonElement value, JsonElement context, bool parametersOnly = false)
    {
        JsonNode? Expand(JsonElement item)
        {
            if (Reference(item, out var pointer))
            {
                if (parametersOnly && Tokens(pointer)[0] == "steps") return JsonNode.Parse(item.GetRawText());
                if (!TryPointer(context, pointer, out var referenced)) throw new DriverException(Error.ObservationUnavailable);
                // Values obtained from parameters/observations are data; never recursively interpret their $ref keys.
                return JsonNode.Parse(referenced.GetRawText());
            }
            if (item.ValueKind == JsonValueKind.Object)
            {
                var obj = new JsonObject(); foreach (var p in item.EnumerateObject()) obj.Add(p.Name, Expand(p.Value)); return obj;
            }
            if (item.ValueKind == JsonValueKind.Array)
            {
                var array = new JsonArray(); foreach (var element in item.EnumerateArray()) array.Add(Expand(element)); return array;
            }
            return JsonNode.Parse(item.GetRawText());
        }
        return JsonSerializer.SerializeToElement(Expand(value), Wire.Json);
    }
    public static void Validate(ScriptCondition condition)
    {
        Tokens(condition.Path);
        if (condition.Op is not ("eq" or "ne" or "gt" or "ge" or "lt" or "le" or "contains" or "exists" or "count-eq" or "count-ge" or "count-le") ||
            condition.Value.ValueKind == JsonValueKind.Undefined) throw new DriverException(Error.InvalidRequest);
        if (Reference(condition.Value, out _)) return;
        if (condition.Op == "exists" && condition.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            condition.Op.StartsWith("count-", StringComparison.Ordinal) && (condition.Value.ValueKind != JsonValueKind.Number ||
                !condition.Value.TryGetInt32(out var count) || count < 0) ||
            condition.Op is "gt" or "ge" or "lt" or "le" && (condition.Value.ValueKind != JsonValueKind.Number ||
                !condition.Value.TryGetDouble(out var number) || !double.IsFinite(number))) throw new DriverException(Error.InvalidRequest);
    }
    public static bool Matches(JsonElement root, ScriptCondition condition)
    {
        var exists = TryPointer(root, condition.Path, out var actual);
        var expected = condition.Value;
        if (condition.Op == "exists")
        {
            if (expected.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new DriverException(Error.InvalidRequest);
            return exists == expected.GetBoolean();
        }
        if (!exists) return false;
        if (condition.Op == "eq") return JsonElement.DeepEquals(actual, expected);
        if (condition.Op == "ne") return !JsonElement.DeepEquals(actual, expected);
        if (condition.Op == "contains") return actual.ValueKind == JsonValueKind.String && expected.ValueKind == JsonValueKind.String
            ? actual.GetString()!.Contains(expected.GetString()!, StringComparison.Ordinal)
            : actual.ValueKind == JsonValueKind.Array && actual.EnumerateArray().Any(a => JsonElement.DeepEquals(a, expected));
        if (condition.Op.StartsWith("count-", StringComparison.Ordinal))
        {
            if (expected.ValueKind != JsonValueKind.Number || !expected.TryGetInt32(out var count) || count < 0) throw new DriverException(Error.InvalidRequest);
            return actual.ValueKind == JsonValueKind.Array && condition.Op switch
            {
                "count-eq" => actual.GetArrayLength() == count, "count-ge" => actual.GetArrayLength() >= count, _ => actual.GetArrayLength() <= count
            };
        }
        if (actual.ValueKind != JsonValueKind.Number || expected.ValueKind != JsonValueKind.Number || !actual.TryGetDouble(out var a) ||
            !expected.TryGetDouble(out var b) || !double.IsFinite(a) || !double.IsFinite(b)) return false;
        return condition.Op switch { "gt" => a > b, "ge" => a >= b, "lt" => a < b, "le" => a <= b, _ => false };
    }
}

using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopTestDriver;

/// <summary>stdio MCP server (JSON-RPC 2.0, one message per line). Tools call the controller host through its pipe,
/// so there is no request file and no process start per call. When no host is running one is started in-process.</summary>
internal static class McpServer
{
    private const string Instructions = """
        DesktopTestDriver drives Windows GUIs through UI Automation: find elements by automationId/name, act on them and read values as strings.
        Workflow: gui_session open (launch an app or name a target) -> gui_window windows -> gui_observe inspect (compact tree, refs like e12 with t=token)
        -> gui_act with elementId+snapshotToken -> gui_wait for the result. Prefer UIA operations (invoke, set-value, set-toggle, select-item)
        over real input; real input (click, type-text, key) needs a session opened with input=true and the window in the foreground.
        Rules: a ref is valid only for the window it came from; StaleElement means re-run find-element or inspect. AmbiguousTarget lists candidates:
        refine the selector or pass index. Waits return completed with satisfied=false on timeout. A completionUnknown result blocks the session:
        check it with gui_session status, then reconcile. Use expect/observeAfter on actions to avoid extra round trips.
        """;
    private static readonly string[] WindowOps = ["windows", "find-window", "window-state", "focus", "close-window", "launch", "open", "terminate-owned"];
    private static readonly string[] ObserveOps = ["inspect", "find-element", "capabilities", "find-item", "read-grid", "read-text", "generate-selector", "clipboard-get"];
    private static readonly string[] ActOps = Operations.All.Where(d => !d.ReadOnly && d.Category is "uia" or "input").Select(d => d.Operation).ToArray();
    private static readonly string[] WaitOps = ["wait-condition", "wait-window"];
    private static JsonObject Schema(string[] operations, string description) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["sessionId"] = new JsonObject { ["type"] = "string", ["description"] = "controlSessionId from gui_session open" },
            ["operation"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(operations.Select(o => (JsonNode)o).ToArray()) },
            ["arguments"] = new JsonObject { ["type"] = "object", ["description"] = description },
            ["timeoutMilliseconds"] = new JsonObject { ["type"] = "integer", ["description"] = "1-5000 (waits, launch and open up to 30000)" },
            ["requestId"] = new JsonObject { ["type"] = "string", ["description"] = "Optional. Reuse the same id to look up or safely repeat a mutation" },
            ["note"] = new JsonObject { ["type"] = "string", ["description"] = "Optional human-readable intent (<=120 chars), kept in the trace" }
        },
        ["required"] = new JsonArray("sessionId", "operation")
    };
    private static JsonArray Tools() => [
        new JsonObject
        {
            ["name"] = "gui_session", ["description"] = "Open, inspect, resume, reconcile or close a control session; list agents (status) and run diagnostics (doctor).",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("open", "close", "status", "resume", "pause", "reconcile", "agents", "doctor", "request-status") },
                    ["machineId"] = new JsonObject { ["type"] = "string", ["description"] = "Agent machine; optional when only one agent is connected" },
                    ["sessionId"] = new JsonObject { ["type"] = "string" },
                    ["input"] = new JsonObject { ["type"] = "boolean", ["description"] = "Allow real keyboard/mouse input (one input session per agent)" },
                    ["observe"] = new JsonObject { ["type"] = "boolean", ["description"] = "Read-only session" },
                    ["launch"] = new JsonObject { ["type"] = "string", ["description"] = "Absolute path of an .exe to start and wait for its first window" },
                    ["launchArguments"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                    ["targets"] = new JsonObject { ["type"] = "object", ["description"] = "Existing windows to allow: {pids:[], executables:[], windowTitle, windowTitleContains, windowClass, any:true}" },
                    ["idleSeconds"] = new JsonObject { ["type"] = "integer" },
                    ["requestId"] = new JsonObject { ["type"] = "string" },
                    ["resolution"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("effect-confirmed", "no-effect-confirmed", "abandoned") },
                    ["resume"] = new JsonObject { ["type"] = "boolean" }
                },
                ["required"] = new JsonArray("action")
            }
        },
        new JsonObject { ["name"] = "gui_window", ["description"] = "List and manage top-level windows; launch an .exe or open URIs, control panel items, .msc/.cpl files and documents (waitWindow returns the new window).", ["inputSchema"] = Schema(WindowOps, "Operation arguments, e.g. {} for windows, {title|titleContains|className|kind} for find-window, {executable, arguments, waitWindow:{timeoutMilliseconds,titleContains}} for launch, {target:'ms-settings:about', waitWindow:{...}} for open") },
        new JsonObject { ["name"] = "gui_observe", ["description"] = "Read UI: inspect returns a compact tree (one line per element with ref and token). find-element takes selector {automationId|name|nameContains|nameRegex|controlType|className|parent|index} or selectors[].", ["inputSchema"] = Schema(ObserveOps, "Always include windowId. inspect: {windowId, maxDepth?, rootElementId?, rootSnapshotToken?, interactiveOnly?, includeBounds?}. find-element: {windowId, selector}") },
        new JsonObject { ["name"] = "gui_act", ["description"] = "Change UI state. UIA: invoke, set-value, set-toggle, select-item, set-expanded, set-range-value, set-focus, legacy-default-action, scroll... Real input (input session only): click, type-text, key, chord, move, wheel, drag, release-input. Optional expect {selector, condition, expected} and observeAfter {maxDepth, includeNewWindows} return the result in the same call.", ["inputSchema"] = Schema(ActOps, "{windowId, elementId, snapshotToken, ...operation fields}") },
        new JsonObject { ["name"] = "gui_wait", ["description"] = "Wait for a condition (exists, absent, enabled, disabled, value-equals, value-not-equals, text-contains, name-equals, name-contains, toggle-equals, selected-equals, expanded-equals, onscreen, offscreen) or anyOf (up to 4), or for a window. Up to 30 s. Timeout returns satisfied=false.", ["inputSchema"] = Schema(WaitOps, "wait-condition: {windowId, selector, condition, expected?} or {windowId, anyOf:[...]}; wait-window: {title|titleContains|className}") },
        new JsonObject
        {
            ["name"] = "gui_capture", ["description"] = "Screenshot of a window client area, a region or one element (scale 0.25-1 to save tokens). Returns the PNG path; image=true also returns the image.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["sessionId"] = new JsonObject { ["type"] = "string" },
                    ["arguments"] = new JsonObject { ["type"] = "object", ["description"] = "{windowId, region?:{x,y,width,height}, elementId?, snapshotToken?, scale?, output?}" },
                    ["image"] = new JsonObject { ["type"] = "boolean" }
                },
                ["required"] = new JsonArray("sessionId", "arguments")
            }
        }
    ];
    public static async Task<int> Run(ControllerSettings settings)
    {
        using var host = new CancellationTokenSource();
        Task? hosted = null;
        var probe = await ControllerHost.Call(settings, new LocalCommand("status"));
        if (probe.Error?.Code == "ControllerUnavailable")
        {
            hosted = Task.Run(() => new ControllerHost(settings).Run(host.Token));
            for (int i = 0; i < 50 && (await ControllerHost.Call(settings, new LocalCommand("status"))).Error?.Code == "ControllerUnavailable"; i++) await Task.Delay(100);
        }
        var output = Console.OpenStandardOutput(); var writeLock = new SemaphoreSlim(1);
        async Task Send(JsonNode message)
        {
            var bytes = Encoding.UTF8.GetBytes(message.ToJsonString(new JsonSerializerOptions { Encoder = GuiProtocol.Json.Encoder }) + "\n");
            await writeLock.WaitAsync(); try { await output.WriteAsync(bytes); await output.FlushAsync(); } finally { writeLock.Release(); }
        }
        var pending = new List<Task>();
        string? line;
        while ((line = await Console.In.ReadLineAsync()) != null)
        {
            line = line.TrimStart('﻿'); if (line.Length == 0) continue;
            JsonNode? message;
            try { message = JsonNode.Parse(line); } catch (JsonException) { await Send(Error(null, -32700, "Parse error")); continue; }
            var id = message?["id"]?.DeepClone(); var method = message?["method"]?.GetValue<string>();
            if (method == null) continue;
            if (id == null) continue; // notifications (initialized, cancelled) need no answer
            pending.Add(Task.Run(async () =>
            {
                try { await Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = await Handle(settings, method, message!["params"]) }); }
                catch (Exception e) { await Send(Error(id, -32603, e.Message)); }
            }));
            pending.RemoveAll(t => t.IsCompleted);
        }
        await Task.WhenAll(pending);
        if (hosted != null) { host.Cancel(); try { await hosted; } catch { } }
        return 0;
    }
    private static JsonObject Error(JsonNode? id, int code, string message) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
    private static async Task<JsonNode> Handle(ControllerSettings settings, string method, JsonNode? p)
    {
        switch (method)
        {
            case "initialize":
                return new JsonObject
                {
                    ["protocolVersion"] = p?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "desktop-test-driver", ["version"] = typeof(ControllerHost).Assembly.GetName().Version?.ToString(3) },
                    ["instructions"] = Instructions
                };
            case "ping": return new JsonObject();
            case "tools/list": return new JsonObject { ["tools"] = Tools() };
            case "tools/call":
            {
                var name = p?["name"]?.GetValue<string>() ?? throw new InvalidOperationException("Tool name required");
                var a = p?["arguments"] as JsonObject ?? new JsonObject();
                var (response, image) = await Call(settings, name, a);
                var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = GuiProtocol.Encode(response) });
                if (image != null) content.Add(new JsonObject { ["type"] = "image", ["data"] = image, ["mimeType"] = "image/png" });
                return new JsonObject { ["content"] = content, ["isError"] = response.Status is not ("completed" or "running") };
            }
            default: throw new InvalidOperationException("Method not found: " + method);
        }
    }
    private static string? S(JsonObject a, string name) => a[name]?.GetValue<string>();
    private static bool B(JsonObject a, string name) => a[name]?.GetValue<bool>() == true;
    private static async Task<(GuiResponse Response, string? Image)> Call(ControllerSettings settings, string tool, JsonObject a)
    {
        if (tool == "gui_session")
        {
            var action = S(a, "action"); var sid = S(a, "sessionId");
            LocalCommand command = action switch
            {
                "open" => new("session-open", MachineId: S(a, "machineId"))
                {
                    Input = B(a, "input"), Mode = B(a, "observe") ? "observe" : "control", Launch = S(a, "launch"),
                    LaunchArguments = a["launchArguments"]?.Deserialize<string[]>(), IdleSeconds = a["idleSeconds"]?.GetValue<int>() ?? 900,
                    Targets = a["targets"]?.Deserialize<TargetSpec>(GuiProtocol.Json)
                },
                "close" => new("session-close", SessionId: sid),
                "status" => new("session-status", SessionId: sid),
                "resume" => new("session-resume", SessionId: sid),
                "pause" => new("session-pause", SessionId: sid),
                "reconcile" => new("session-reconcile", SessionId: sid, RequestId: S(a, "requestId")) { Resolution = S(a, "resolution"), Resume = B(a, "resume") },
                "request-status" => new("request-status", SessionId: sid, RequestId: S(a, "requestId")),
                "agents" => new("status"),
                "doctor" => new("doctor", MachineId: S(a, "machineId")),
                _ => throw new InvalidOperationException("Unknown action")
            };
            return (await ControllerHost.Call(settings, command), null);
        }
        var operation = tool == "gui_capture" ? "capture" : S(a, "operation") ?? throw new InvalidOperationException("operation required");
        var allowed = tool switch { "gui_window" => WindowOps, "gui_observe" => ObserveOps, "gui_act" => ActOps, "gui_wait" => WaitOps, "gui_capture" => ["capture"], _ => throw new InvalidOperationException("Unknown tool") };
        if (!allowed.Contains(operation)) throw new InvalidOperationException(operation + " is not part of " + tool);
        var request = new JsonObject
        {
            ["requestId"] = S(a, "requestId") ?? "mcp-" + Guid.NewGuid().ToString("N")[..16],
            ["controlSessionId"] = S(a, "sessionId") ?? throw new InvalidOperationException("sessionId required"),
            ["operation"] = operation,
            ["arguments"] = a["arguments"]?.DeepClone() ?? new JsonObject()
        };
        if (a["timeoutMilliseconds"] is { } timeout) request["timeoutMilliseconds"] = timeout.DeepClone();
        if (S(a, "note") is { } note) request["note"] = note;
        var response = await ControllerHost.Call(settings, new LocalCommand("request", Request: request.ToJsonString()));
        string? image = null;
        if (tool == "gui_capture" && B(a, "image") && response.Status == "completed" && GuiProtocol.Element(response.Data).TryGetProperty("path", out var path))
            image = Convert.ToBase64String(await File.ReadAllBytesAsync(path.GetString()!));
        return (response, image);
    }
}

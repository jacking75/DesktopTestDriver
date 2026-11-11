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
        DesktopTestDriver drives Windows GUIs through UI Automation: find elements by automationId/name, act on them, read values as strings.
        Prerequisite: Agent.exe must run in the logged-on desktop of the target PC. gui_session {action:"agents"} lists connected agents;
        if none, ask the user to start Agent.exe. {action:"doctor"} checks UIA, capture and input on the agent.
        Workflow: gui_session open {launch:"C:\\path\\App.exe"} returns data.launch.window.windowId -> gui_observe inspect {windowId}
        (compact tree; "[e12 t=1d93122bc0be]" = elementId e12, snapshotToken 1d93122bc0be) -> gui_act {windowId, elementId, snapshotToken, ...}
        -> gui_wait wait-condition -> judge the product result -> gui_session close.
        Request fields: put operation fields in "arguments"; timeoutMilliseconds, requestId and note are tool parameters, not arguments.
        Prefer UIA operations over real input. Real input (click, type-text, key, chord) needs open with input=true and a foreground window.
        Errors: StaleElement -> find again. AmbiguousTarget -> error.data.candidates, narrow the selector or add index. SearchIncomplete -> use an
        automationId outside lists or arguments.completeness="realized". A wait timeout is completed with satisfied=false.
        completionUnknown blocks the session: do not repeat the action; observe, then gui_session reconcile {requestId, resolution, resume:true}.
        Argument errors repeat the expected arguments of the operation. Use expect/observeAfter on actions to save round trips.
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
    /// <summary>A request tool whose description carries the argument reference of each of its operations.</summary>
    private static JsonObject Tool(string name, string summary, string[] operations) => new()
    {
        ["name"] = name,
        ["description"] = summary + "\nArguments per operation (? = optional):\n" + OperationHelp.Reference(operations),
        ["inputSchema"] = Schema(operations, "Operation fields as listed in the tool description, e.g. {\"windowId\":\"w1\",\"elementId\":\"e4\",\"snapshotToken\":\"1d93122bc0be\"}")
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
        Tool("gui_window", "List and manage top-level windows; start an .exe (launch) or open a URI, control panel item, .msc/.cpl or document (open). New windows join the session scope.", WindowOps),
        Tool("gui_observe", "Read the UI. inspect returns a compact tree, one element per line: `- Button #SaveButton \"저장\" [e4 t=1d93122bc0be] patterns=Invoke` (#=automationId, e4=elementId, t=snapshotToken). Refs are valid only for that window.", ObserveOps),
        Tool("gui_act", "Change UI state. Prefer UIA operations; real input operations need a session opened with input=true. expect/observeAfter return the effect in the same call. A rejected status means nothing happened.", ActOps),
        Tool("gui_wait", "Wait until a condition holds (or the first of anyOf), or until a window appears. Set timeoutMilliseconds (tool parameter, up to 30000). A timeout returns completed with satisfied=false, not an error.", WaitOps),
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
                // Trees are also given unescaped so they read as indented lines instead of one string full of \n.
                foreach (var (label, tree) in Trees(response)) content.Add(new JsonObject { ["type"] = "text", ["text"] = label + "\n" + tree });
                if (image != null) content.Add(new JsonObject { ["type"] = "image", ["data"] = image, ["mimeType"] = "image/png" });
                return new JsonObject { ["content"] = content, ["isError"] = response.Status is not ("completed" or "running") };
            }
            default: throw new InvalidOperationException("Method not found: " + method);
        }
    }
    private static IEnumerable<(string Label, string Tree)> Trees(GuiResponse response)
    {
        static string? Text(JsonElement e, params string[] path)
        {
            foreach (var p in path) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null; }
            return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        }
        if (response.Data != null)
        {
            var data = GuiProtocol.Element(response.Data);
            if (Text(data, "tree") is { Length: > 0 } tree) yield return ("tree:", tree);
            if (Text(data, "after", "tree") is { Length: > 0 } after) yield return ("after (observeAfter):", after);
        }
        if (response.Error?.Data != null && Text(GuiProtocol.Element(response.Error.Data), "context", "tree") is { Length: > 0 } context)
            yield return ("context (top of the window at the time of the error):", context);
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

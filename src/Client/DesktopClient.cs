using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopTestDriver;

/// <summary>An element reference returned by find-element or inspect.</summary>
public sealed record ElementRef(string WindowId, string ElementId, string SnapshotToken);
/// <summary>Typed reads over a response's data.</summary>
public static class ResponseExtensions
{
    public static JsonElement Json(this GuiResponse r) => r.Data == null ? default : GuiProtocol.Element(r.Data);
    public static GuiResponse EnsureCompleted(this GuiResponse r) =>
        r.Status == "completed" ? r : throw new GuiException(r.Error?.Code ?? r.Status, $"{r.Operation} {r.Status}: {r.Error?.Code} {r.Error?.Message}", r.Error?.Data);
    /// <summary>The target node of find-element, capabilities, wait-condition or an element mutation.</summary>
    public static ObservationNode? Target(this GuiResponse r) =>
        r.Json() is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.Object ? t.Deserialize<ObservationNode>(GuiProtocol.Json) : null;
    public static ElementRef Ref(this GuiResponse r, string windowId) => r.Target() is { } t ? new(windowId, t.ElementId, t.SnapshotToken) : throw new GuiException("ElementNotFound", "Response has no target");
    /// <summary>The compact tree text of inspect.</summary>
    public static string Tree(this GuiResponse r) => r.Json().GetProperty("tree").GetString() ?? "";
    public static PublicWindow Window(this GuiResponse r) => r.Json().GetProperty("window").Deserialize<PublicWindow>(GuiProtocol.Json)!;
    public static PublicWindow[] Windows(this GuiResponse r) => r.Json().GetProperty("windows").Deserialize<PublicWindow[]>(GuiProtocol.Json)!;
    public static bool Satisfied(this GuiResponse r) => r.Json().TryGetProperty("satisfied", out var s) && s.GetBoolean();
    public static string ControlSessionId(this GuiResponse r) => r.ControlSessionId ?? throw new GuiException("SessionNotFound");
}
/// <summary>C# client for the controller host. Typical use: open a session, find by selector, act, wait, verify —
/// the same flow an agent explored, replayed without a model.</summary>
public sealed class DesktopClient(ControllerSettings settings)
{
    private static readonly JsonSerializerOptions Lean = new(GuiProtocol.Json) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public DesktopClient() : this(new ControllerSettings(HostSettings.DefaultState("Controller"))) { }
    public Task<GuiResponse> Call(LocalCommand command, CancellationToken cancellation = default) => ControllerHost.Call(settings, command, cancellation);
    // ---------------------------------------------------------------- sessions
    public Task<GuiResponse> OpenSessionAsync(string? machine = null, TargetSpec? targets = null, bool input = false, bool observe = false, string? launch = null, string[]? launchArguments = null, int idleSeconds = 900, CancellationToken cancellation = default) =>
        Call(new("session-open", MachineId: machine) { Targets = targets, Input = input, Mode = observe ? "observe" : "control", Launch = launch, LaunchArguments = launchArguments, IdleSeconds = idleSeconds }, cancellation);
    public Task<GuiResponse> CloseSessionAsync(string session, CancellationToken cancellation = default) => Call(new("session-close", SessionId: session), cancellation);
    public Task<GuiResponse> SessionStatusAsync(string session, CancellationToken cancellation = default) => Call(new("session-status", SessionId: session), cancellation);
    public Task<GuiResponse> ResumeAsync(string session, long? expectedRevision = null, CancellationToken cancellation = default) => Call(new("session-resume", SessionId: session) { ExpectedRevision = expectedRevision }, cancellation);
    public Task<GuiResponse> PauseAsync(string session, CancellationToken cancellation = default) => Call(new("session-pause", SessionId: session), cancellation);
    public Task<GuiResponse> ReconcileAsync(string session, string requestId, string resolution, bool resume = true, CancellationToken cancellation = default) => Call(new("session-reconcile", SessionId: session, RequestId: requestId) { Resolution = resolution, Resume = resume }, cancellation);
    public Task<GuiResponse> GetRequestResultAsync(string session, string requestId, CancellationToken cancellation = default) => Call(new("request-status", SessionId: session, RequestId: requestId), cancellation);
    // ---------------------------------------------------------------- generic request
    public async Task<GuiResponse> Execute(string session, string operation, object arguments, CancellationToken cancellation = default, string? requestId = null, int timeoutMilliseconds = 5000, string? note = null)
    {
        cancellation.ThrowIfCancellationRequested();
        var request = new GuiRequest(requestId ?? "r-" + Guid.NewGuid().ToString("N")[..20], session, operation, timeoutMilliseconds, JsonSerializer.SerializeToElement(arguments, Lean), note);
        var json = GuiProtocol.Encode(request); GuiProtocol.Parse(json);
        // Cancellation stops waiting; it never authorizes replaying an uncertain mutation.
        try { return await Call(new("request", Request: json), cancellation); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return new(GuiProtocol.ReadOnly(operation) ? "rejected" : "completionUnknown", request.RequestId, session, Error: new("ClientWaitCanceled", "ClientWaitCanceled")) { Operation = operation };
        }
    }
    // ---------------------------------------------------------------- windows and applications
    public Task<GuiResponse> WindowsAsync(string session, bool includePopups = false, CancellationToken cancellation = default) => Execute(session, "windows", new { includePopups = includePopups ? true : (bool?)null }, cancellation);
    public Task<GuiResponse> FindWindowAsync(string session, string? title = null, string? titleContains = null, string? className = null, string? kind = null, CancellationToken cancellation = default) =>
        Execute(session, "find-window", new { title, titleContains, className, kind }, cancellation);
    public Task<GuiResponse> WaitWindowAsync(string session, string? title = null, string? titleContains = null, string? className = null, int timeoutMilliseconds = 10000, CancellationToken cancellation = default) =>
        Execute(session, "wait-window", new { title, titleContains, className }, cancellation, timeoutMilliseconds: timeoutMilliseconds);
    public Task<GuiResponse> LaunchAsync(string session, string executable, string[]? arguments = null, string lifetime = "session", int waitWindowMilliseconds = 10000, string? requestId = null, CancellationToken cancellation = default) =>
        Execute(session, "launch", new { executable, arguments = arguments ?? [], lifetime, waitWindow = waitWindowMilliseconds > 0 ? new { timeoutMilliseconds = waitWindowMilliseconds } : null }, cancellation, requestId, timeoutMilliseconds: Math.Max(5000, waitWindowMilliseconds + 2000));
    public Task<GuiResponse> OpenAsync(string session, string target, string? titleContains = null, int waitWindowMilliseconds = 10000, string? requestId = null, CancellationToken cancellation = default) =>
        Execute(session, "open", new { target, waitWindow = new { timeoutMilliseconds = waitWindowMilliseconds, titleContains } }, cancellation, requestId, timeoutMilliseconds: Math.Max(5000, waitWindowMilliseconds + 2000));
    public Task<GuiResponse> WindowStateAsync(string session, string window, string state, CancellationToken cancellation = default) => Execute(session, "window-state", new { windowId = window, state }, cancellation);
    public Task<GuiResponse> FocusAsync(string session, string window, CancellationToken cancellation = default) => Execute(session, "focus", new { windowId = window }, cancellation);
    public Task<GuiResponse> CloseWindowAsync(string session, string window, CancellationToken cancellation = default) => Execute(session, "close-window", new { windowId = window }, cancellation);
    // ---------------------------------------------------------------- observation
    public Task<GuiResponse> InspectAsync(string session, string window, int? maxDepth = null, ElementRef? root = null, bool interactiveOnly = false, bool includeBounds = false, string format = "compact", CancellationToken cancellation = default) =>
        Execute(session, "inspect", new { windowId = window, maxDepth, rootElementId = root?.ElementId, rootSnapshotToken = root?.SnapshotToken, interactiveOnly = interactiveOnly ? true : (bool?)null, includeBounds = includeBounds ? true : (bool?)null, format }, cancellation);
    public Task<GuiResponse> FindElementAsync(string session, string window, ElementSelector selector, ElementRef? searchRoot = null, string? completeness = null, CancellationToken cancellation = default) =>
        Execute(session, "find-element", new { windowId = window, selector, searchRootElementId = searchRoot?.ElementId, searchRootSnapshotToken = searchRoot?.SnapshotToken, completeness }, cancellation);
    public Task<GuiResponse> FindElementsAsync(string session, string window, ElementSelector[] selectors, CancellationToken cancellation = default) => Execute(session, "find-element", new { windowId = window, selectors }, cancellation);
    public Task<GuiResponse> ReadElementAsync(ElementRef element, string session, CancellationToken cancellation = default) => Execute(session, "capabilities", new { windowId = element.WindowId, elementId = element.ElementId, snapshotToken = element.SnapshotToken }, cancellation);
    public Task<GuiResponse> ReadTextAsync(string session, ElementRef element, CancellationToken cancellation = default) => Execute(session, "read-text", Args(element), cancellation);
    public Task<GuiResponse> ReadGridAsync(string session, ElementRef element, int startRow = 0, int rowCount = 50, CancellationToken cancellation = default) => Execute(session, "read-grid", new { windowId = element.WindowId, elementId = element.ElementId, snapshotToken = element.SnapshotToken, startRow, rowCount }, cancellation);
    public Task<GuiResponse> GenerateSelectorAsync(string session, ElementRef element, CancellationToken cancellation = default) => Execute(session, "generate-selector", Args(element), cancellation);
    public Task<GuiResponse> CaptureAsync(string session, string window, string? output = null, PixelRect? region = null, ElementRef? element = null, double? scale = null, CancellationToken cancellation = default) =>
        Execute(session, "capture", new { windowId = window, output, region, elementId = element?.ElementId, snapshotToken = element?.SnapshotToken, scale }, cancellation);
    /// <summary>Waits for a condition. Polls every <paramref name="pollMilliseconds"/>; a timeout returns satisfied=false, not an error.</summary>
    public Task<GuiResponse> WaitAsync(string session, string window, ElementSelector selector, string condition, string? expected = null, int timeoutMilliseconds = 5000, int pollMilliseconds = 100, ElementRef? searchRoot = null, CancellationToken cancellation = default) =>
        Execute(session, "wait-condition", new { windowId = window, selector, condition, expected, pollMilliseconds, searchRootElementId = searchRoot?.ElementId, searchRootSnapshotToken = searchRoot?.SnapshotToken }, cancellation, timeoutMilliseconds: timeoutMilliseconds);
    public Task<GuiResponse> WaitAnyAsync(string session, string window, (ElementSelector Selector, string Condition, string? Expected)[] anyOf, int timeoutMilliseconds = 5000, int pollMilliseconds = 100, CancellationToken cancellation = default) =>
        Execute(session, "wait-condition", new { windowId = window, anyOf = anyOf.Select(x => new { selector = x.Selector, condition = x.Condition, expected = x.Expected }).ToArray(), pollMilliseconds }, cancellation, timeoutMilliseconds: timeoutMilliseconds);
    // ---------------------------------------------------------------- UIA actions
    private static object Args(ElementRef e) => new { windowId = e.WindowId, elementId = e.ElementId, snapshotToken = e.SnapshotToken };
    private Task<GuiResponse> Act(string session, string operation, ElementRef e, object? extra, string? requestId, CancellationToken cancellation)
    {
        var args = JsonSerializer.SerializeToNode(Args(e))!.AsObject();
        if (extra != null) foreach (var p in JsonSerializer.SerializeToNode(extra, Lean)!.AsObject()) args[p.Key] = p.Value?.DeepClone();
        return Execute(session, operation, args, cancellation, requestId);
    }
    public Task<GuiResponse> InvokeAsync(string session, ElementRef e, string? requestId = null, CancellationToken cancellation = default) => Act(session, "invoke", e, null, requestId, cancellation);
    public Task<GuiResponse> SetValueAsync(string session, ElementRef e, string value, string? requestId = null, CancellationToken cancellation = default) => Act(session, "set-value", e, new { value }, requestId, cancellation);
    public Task<GuiResponse> SetRangeValueAsync(string session, ElementRef e, double value, string? requestId = null, CancellationToken cancellation = default) => Act(session, "set-range-value", e, new { value }, requestId, cancellation);
    public Task<GuiResponse> SetToggleAsync(string session, ElementRef e, string desiredState, string? requestId = null, CancellationToken cancellation = default) => Act(session, "set-toggle", e, new { desiredState }, requestId, cancellation);
    public Task<GuiResponse> SelectAsync(string session, ElementRef e, string? requestId = null, CancellationToken cancellation = default) => Act(session, "select-item", e, null, requestId, cancellation);
    public Task<GuiResponse> SetExpandedAsync(string session, ElementRef e, bool expanded, string? requestId = null, CancellationToken cancellation = default) => Act(session, "set-expanded", e, new { expanded }, requestId, cancellation);
    public Task<GuiResponse> ScrollIntoViewAsync(string session, ElementRef e, string? requestId = null, CancellationToken cancellation = default) => Act(session, "scroll-item-into-view", e, null, requestId, cancellation);
    public Task<GuiResponse> SetFocusAsync(string session, ElementRef e, string? requestId = null, CancellationToken cancellation = default) => Act(session, "set-focus", e, null, requestId, cancellation);
    // ---------------------------------------------------------------- real input (input sessions)
    public Task<GuiResponse> ClickAsync(string session, ElementRef e, int count = 1, string button = "left", string[]? modifiers = null, string? requestId = null, CancellationToken cancellation = default) => Act(session, "click", e, new { count, button, modifiers }, requestId, cancellation);
    public Task<GuiResponse> TypeTextAsync(string session, string window, string text, string? requestId = null, CancellationToken cancellation = default) => Execute(session, "type-text", new { windowId = window, text }, cancellation, requestId);
    public Task<GuiResponse> KeyAsync(string session, string window, string key, string action = "press", string? requestId = null, CancellationToken cancellation = default) => Execute(session, "key", new { windowId = window, key, action }, cancellation, requestId);
    public Task<GuiResponse> ChordAsync(string session, string window, string[] keys, string? requestId = null, CancellationToken cancellation = default) => Execute(session, "chord", new { windowId = window, keys }, cancellation, requestId);
    public Task<GuiResponse> ReleaseInputAsync(string session, CancellationToken cancellation = default) => Execute(session, "release-input", new { }, cancellation);
}

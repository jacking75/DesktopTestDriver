using System.Text.Json;

namespace DesktopTestDriver;

/// <param name="Category">window, application, observe, uia, input</param>
/// <param name="Input">Requires a session opened with --input.</param>
/// <param name="MaxTimeout">Per-request timeout upper bound in milliseconds.</param>
public sealed record OperationDescriptor(string Operation, string Category, bool ReadOnly, string[] RequiredPatterns, bool Input = false, int MaxTimeout = 5000, bool UsesWindow = true)
{
    /// <summary>Mutations and captures are journaled for duplicate suppression and result lookup. Plain reads are not.</summary>
    public bool Journaled => !ReadOnly || Operation == "capture";
}
public static class Operations
{
    public static readonly OperationDescriptor[] All = [
        new("windows", "window", true, [], UsesWindow: false),
        new("find-window", "window", true, [], UsesWindow: false),
        new("wait-window", "window", true, [], MaxTimeout: 30000, UsesWindow: false),
        new("window-state", "window", false, []),
        new("focus", "window", false, []),
        new("close-window", "window", false, []),
        new("launch", "application", false, [], MaxTimeout: 30000, UsesWindow: false),
        new("open", "application", false, [], MaxTimeout: 30000, UsesWindow: false),
        new("terminate-owned", "application", false, [], UsesWindow: false),
        new("inspect", "observe", true, []),
        new("find-element", "observe", true, []),
        new("capabilities", "observe", true, []),
        new("wait-condition", "observe", true, [], MaxTimeout: 30000),
        new("find-item", "observe", true, ["ItemContainer"]),
        new("read-grid", "observe", true, ["Grid"]),
        new("read-text", "observe", true, []),
        new("generate-selector", "observe", true, []),
        new("capture", "observe", true, []),
        new("clipboard-get", "observe", true, [], UsesWindow: false),
        new("invoke", "uia", false, ["Invoke"]),
        new("set-value", "uia", false, ["Value"]),
        new("set-range-value", "uia", false, ["RangeValue"]),
        new("select-item", "uia", false, ["SelectionItem"]),
        new("set-toggle", "uia", false, ["Toggle"]),
        new("set-expanded", "uia", false, ["ExpandCollapse"]),
        new("set-focus", "uia", false, []),
        new("legacy-default-action", "uia", false, ["LegacyIAccessible"]),
        new("realize-item", "uia", false, ["VirtualizedItem"]),
        new("scroll-item-into-view", "uia", false, ["ScrollItem"]),
        new("scroll-container", "uia", false, ["Scroll"]),
        new("clipboard-set", "uia", false, [], UsesWindow: false),
        new("click", "input", false, [], Input: true),
        new("type-text", "input", false, [], Input: true),
        new("key", "input", false, [], Input: true),
        new("chord", "input", false, [], Input: true),
        new("move", "input", false, [], Input: true),
        new("wheel", "input", false, [], Input: true),
        new("drag", "input", false, [], Input: true),
        new("release-input", "input", false, [], Input: true, UsesWindow: false)
    ];
    private static readonly Dictionary<string, OperationDescriptor> byName = All.ToDictionary(d => d.Operation, StringComparer.Ordinal);
    public static OperationDescriptor? Find(string operation) => byName.GetValueOrDefault(operation);
    public static OperationDescriptor Get(string operation) => Find(operation) ?? throw new GuiException("UnsupportedOperation");
    public static bool Contains(string operation) => byName.ContainsKey(operation);
    /// <summary>UIA element mutations sent to the per-session automation worker.</summary>
    public static bool ElementMutation(string operation) => Find(operation) is { Category: "uia" } && operation != "clipboard-set";
}
public sealed record ElementSelector(string? AutomationId = null, string? AutomationIdRegex = null, string? ControlType = null,
    string? Name = null, string? NameContains = null, string? NameStartsWith = null, string? NameRegex = null,
    string? ClassName = null, string? FrameworkId = null, string? AncestorAutomationId = null, ElementSelector? Parent = null, int? Index = null);
public sealed record RangeInfo(double Value, double Minimum, double Maximum);
/// <summary>One UIA element. Inside the agent, ElementId is the UIA runtime id and SnapshotToken is the full hash;
/// responses carry a session reference (e12) and a 12 character token instead.</summary>
public sealed record ObservationNode(string ElementId, string? ParentId, string AutomationId, string Name, string ControlType,
    string ClassName, string FrameworkId, bool Enabled, bool Offscreen, ElementValue Value, string[] Patterns, string SnapshotToken)
{
    public string? ToggleState { get; init; }
    public PixelRect? Bounds { get; init; }
    public bool? Selected { get; init; }
    public string? ExpandState { get; init; }
    public bool? Focused { get; init; }
    public RangeInfo? RangeValue { get; init; }
    public string? HelpText { get; init; }
    public string? ItemStatus { get; init; }
    public bool? NameTruncated { get; init; }
}
public sealed record ElementLocator(string RuntimeId, string Token, string TreeView = "control");
public sealed record DiagnosticItem(string Name, string State, string Reason, string? NextAction = null);
/// <summary>Command for the per-session UIA worker process.</summary>
public sealed record UiaCommand(DesktopWindow Window, string Operation, JsonElement Arguments)
{
    public string TreeView { get; init; } = "control";
    public ElementLocator? Target { get; init; }
    public ElementLocator? SearchRoot { get; init; }
    public ElementLocator[]? Elements { get; init; }
    public ElementSelector? Selector { get; init; }
    public ElementSelector[]? Selectors { get; init; }
    public string Completeness { get; init; } = "default";
    public PixelRect? HitPoint { get; init; }
}
public sealed record SelectorResult(ObservationNode? Target, GuiError? Error, int MatchCount = 0, string? UniquenessScope = null)
{
    public ObservationNode[]? Path { get; init; }
}
public sealed record UiaResult(ObservationNode[] Nodes, ObservationNode? Target = null)
{
    public bool Complete { get; init; } = true;
    public string? TruncationReason { get; init; }
    public string? UniquenessScope { get; init; }
    public int MatchCount { get; init; }
    public ObservationNode[]? VirtualizedContainers { get; init; }
    public SelectorResult[]? Results { get; init; }
    public JsonElement? Data { get; init; }
    public string? DispatchKind { get; init; }
}
public sealed record WorkerReply(bool Ok, JsonElement? Data = null, GuiError? Error = null, bool DispatchStarted = false);

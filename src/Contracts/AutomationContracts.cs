namespace DesktopTestDriver;

public sealed record OperationDescriptor(string Operation, string Backend, bool ReadOnly,
    string[] RequiredPatterns, bool MaySendGlobalInput = false, bool MayActivateWindow = false,
    bool MayOpenWindow = false, bool MayChangeView = false, string ReplaySafety = "journal-only", int TimeoutLimit = 5000);
public sealed record ElementSelector(string? AutomationId = null, string? ControlType = null,
    string? Name = null, string? ClassName = null, string? FrameworkId = null,
    string? AncestorAutomationId = null, ElementSelector? Parent = null);
public sealed record ObservationNode(string ElementId, string? ParentId, string AutomationId, string Name,
    string ControlType, string ClassName, string FrameworkId, bool Enabled, bool Offscreen,
    ElementValue Value, string[] Patterns, string SnapshotToken, string? ToggleState = null)
{
    public string DetailState {get;init;}="complete";
}
public sealed record Observation(string Root, ObservationNode[] Nodes, DateTimeOffset QueriedAt,
    string TreeView, bool Complete, string? TruncationReason, string[] ProviderErrors, string VirtualizationState)
{
    public bool QueryComplete {get;init;}
}
public sealed record ApplicationBindings(string ExecutableBinding, string WorkingDirectoryBinding, string FingerprintBinding);
public sealed record ProfileAction(string Target, string Operation);
public sealed record ApplicationProfile(int ProfileVersion, string ProfileId, string Backend,
    ApplicationBindings Application, Dictionary<string, ElementSelector> Targets,
    Dictionary<string, ProfileAction> Actions, string? BackgroundQualificationRef = null, string[]? RedactedAutomationIds = null, bool CaptureAllowed = false);
public sealed record MachineBindings(Dictionary<string, string> Paths, Dictionary<string, string> Fingerprints);
public sealed record DiagnosticItem(string Name, string State, string Reason, string? NextAction = null);
public sealed record UiaCommand(DesktopWindow Window, string Operation, ElementSelector? Selector = null,
    string? RuntimeId = null, string? SnapshotToken = null, string? Value = null,
    string? DesiredState = null, bool? Expanded = null, double? Horizontal = null, double? Vertical = null,
    string TreeView = "control", string ScopeSession = "default", string? ItemName = null)
{
    public bool RootOnly {get;init;}
    public string? SearchRootRuntimeId {get;init;}
    public string? SearchRootSnapshotToken {get;init;}
    public string? IntendedOperation {get;init;}
}
public sealed record UiaResult(Observation Observation, ObservationNode? Target = null, string EffectState = "none")
{
    public string LookupKind {get;init;}="full-tree";
    public string? DispatchKind {get;init;}
}

public static class AutomationOperations
{
    public static readonly OperationDescriptor[] All = [
        new("find-window", "windows-observation", true, []), new("wait-window", "windows-observation", true, []),
        new("inspect", "windows-uia", true, []), new("capabilities", "windows-uia", true, []),
        new("find-element", "windows-uia", true, []), new("wait-condition", "windows-uia", true, []),
        new("find-item", "windows-uia", true, ["ItemContainer"]),
        new("invoke", "windows-uia", false, ["Invoke"], MayOpenWindow: true),
        new("set-value", "windows-uia", false, ["Value"]),
        new("select-item", "windows-uia", false, ["SelectionItem"], MayChangeView: true),
        new("set-toggle", "windows-uia", false, ["Toggle"]),
        new("set-expanded", "windows-uia", false, ["ExpandCollapse"], MayChangeView: true),
        new("realize-item", "windows-uia", false, ["VirtualizedItem"], MayChangeView: true),
        new("scroll-item-into-view", "windows-uia", false, ["ScrollItem"], MayChangeView: true),
        new("scroll-container", "windows-uia", false, ["Scroll"], MayChangeView: true)
    ];
    public static bool Contains(string op) => All.Any(x => x.Operation == op);
    public static bool ReadOnly(string op) => All.Any(x => x.Operation == op && x.ReadOnly);
}

namespace DesktopTestDriver;

/// <summary>One-line argument reference per operation. Shown in MCP tool descriptions, in `Controller.exe --operations`
/// and in argument errors, so a coding agent can correct a request without opening the documentation.
/// `?` marks optional fields. E = windowId, elementId, snapshotToken (a reference from inspect/find-element).</summary>
public static class OperationHelp
{
    private const string Element = "windowId, elementId, snapshotToken";
    private const string After = " [expect?{selector,condition,expected?,timeoutMilliseconds?}, observeAfter?{maxDepth?,includeNewWindows?,settleMilliseconds?,rootElementId?,rootSnapshotToken?}] — result: target = the element BEFORE the action (use expect to read the outcome), dispatchKind; if the action opened a modal window and is waiting on it: dispatchState=pending-modal, modalWindows (answer that window)";
    private const string Selector = "selector{automationId|automationIdRegex|name|nameContains|nameStartsWith|nameRegex|controlType|className|frameworkId|ancestorAutomationId|parent{…}|index}";
    private const string WindowFilter = "title?|titleContains?|titleRegex?|className?|kind?(main|owned|popup)|processName?|ownerWindowId? (at least one)";
    public static readonly IReadOnlyDictionary<string, string> Arguments = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["windows"] = "kind?(main|owned|popup), includePopups? → windows[] {windowId, title, pid, processName, bounds, className, kind, ownerWindowId}",
        ["find-window"] = WindowFilter + " → window",
        ["wait-window"] = WindowFilter + ", absent?(true = wait until no window matches, e.g. a dialog closed), pollMilliseconds? → satisfied, window (timeout = request timeoutMilliseconds, up to 30000)",
        ["window-state"] = "windowId, state(minimize|maximize|restore|close)",
        ["focus"] = "windowId → foreground",
        ["close-window"] = "windowId, waitMilliseconds?(0-5000, default 2000) → closed, processExited, newWindows (a prompt such as \"save changes?\" when closed=false)",
        ["launch"] = "executable(absolute path), arguments?[], lifetime?(session|keep), workingDirectory?, verb?(open|runas), waitWindow?{timeoutMilliseconds,title,titleContains,className} → processId, pid, window, waitSatisfied (waitWindow time is capped by the request timeoutMilliseconds: set both)",
        ["open"] = "target(URI like ms-settings:about, control.exe, x.msc, x.cpl, document), arguments?[], verb?(open|runas), workingDirectory?, waitWindow? → window, waitSatisfied (waitWindow time is capped by the request timeoutMilliseconds)",
        ["terminate-owned"] = "processIds[] (processId values from launch)",
        ["inspect"] = "windowId, maxDepth?(1-16), rootElementId?+rootSnapshotToken?, interactiveOnly?, includeOffscreen?, includeBounds?, format?(compact|json), treeView?(control|raw|content) → tree, nodeCount, complete",
        ["find-element"] = "windowId, " + Selector + " or selectors[](≤16), searchRootElementId?+searchRootSnapshotToken?, completeness?(default|realized), includePath? → target{elementId,snapshotToken,value,…}, matchCount, uniquenessScope",
        ["capabilities"] = "windowId and one of: elementId+snapshotToken | selector | elements[]{elementId,snapshotToken}; includeDescriptors? → target (fresh value and state)",
        ["wait-condition"] = "windowId, " + Selector + ", condition(exists|absent|enabled|disabled|value-equals|value-not-equals|text-contains|name-equals|name-contains|toggle-equals|selected-equals|expanded-equals|onscreen|offscreen), expected?(string) — or anyOf[](≤4){selector,condition,expected?}; pollMilliseconds? → satisfied, matchedIndex?, target",
        ["find-item"] = Element + " (of the list), itemName → target (usable with scroll-item-into-view, realize-item, select-item)",
        ["read-grid"] = Element + ", startRow?, rowCount?(≤500 cells), startColumn?, columnCount? → rowCount, columnCount, columnHeaders, rows",
        ["read-text"] = Element + ", selectionOnly?, maxLength? → text, source, truncated",
        ["generate-selector"] = Element + " → selector, alternatives (store these instead of refs in replayable code)",
        ["capture"] = "windowId, region?{x,y,width,height} or elementId+snapshotToken, scale?(0.25-1), output?(absolute .png path) → path, imageWidth, imageHeight, captureId",
        ["clipboard-get"] = "(none) → text",
        ["invoke"] = Element + After,
        ["set-value"] = Element + ", value, expectedValueHash?" + After,
        ["set-range-value"] = Element + ", value(number)" + After,
        ["select-item"] = Element + After,
        ["set-toggle"] = Element + ", desiredState(on|off|indeterminate)" + After,
        ["set-expanded"] = Element + ", expanded(true|false)" + After,
        ["set-focus"] = Element + After,
        ["legacy-default-action"] = Element + After,
        ["realize-item"] = Element + After,
        ["scroll-item-into-view"] = Element + After,
        ["scroll-container"] = Element + ", horizontal(0-100, -1 keeps), vertical(0-100, -1 keeps)" + After,
        ["clipboard-set"] = "text",
        ["click"] = "windowId and elementId+snapshotToken or position{x,y,captureId?}; button?(left|right|middle), count?(1-3), modifiers?[CTRL|SHIFT|ALT]" + After + " — input session",
        ["type-text"] = "windowId, text (window must be foreground)" + After + " — input session",
        ["key"] = "windowId, key(A, F5, ENTER, CTRL, NUMPAD1, =, …), action?(press|down|up)" + After + " — input session",
        ["chord"] = "windowId, keys[] e.g. [\"CTRL\",\"S\"]" + After + " — input session",
        ["move"] = "windowId, position{x,y,captureId?} — input session",
        ["wheel"] = "windowId, position{x,y,captureId?}, delta(±1200) — input session",
        ["drag"] = "windowId, start{x,y}, end{x,y}, captureId?, button?, steps?(1-100) — input session",
        ["release-input"] = "(none) — releases keys and buttons this session holds"
    };
    /// <summary>Includes the request timeout limit, the most common mistake when a request is built by hand.</summary>
    public static string For(string operation) => (Arguments.TryGetValue(operation, out var text) ? operation + ": " + text : operation) + (Operations.Find(operation) is { } d ? " (timeoutMilliseconds ≤ " + d.MaxTimeout + ")" : "");
    public static string Reference(IEnumerable<string> operations) => string.Join("\n", operations.Select(For));
}

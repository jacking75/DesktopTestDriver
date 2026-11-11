using System.Text.Json;

namespace DesktopTestDriver;

/// <summary>Rejects malformed arguments before anything runs. One table for every operation.</summary>
public static class ArgumentValidator
{
    private const string Element = "windowId elementId snapshotToken";
    private const string After = " expect observeAfter errorContext";
    public static readonly string[] Conditions = ["exists", "absent", "enabled", "disabled", "value-equals", "value-not-equals", "text-contains", "name-equals", "name-contains", "toggle-equals", "selected-equals", "expanded-equals", "onscreen", "offscreen"];
    private static string Allowed(string operation) => operation switch
    {
        "windows" => "includePopups kind",
        "find-window" or "wait-window" => "title titleContains titleRegex className kind ownerWindowId processName pollMilliseconds",
        "window-state" => "windowId state",
        "focus" or "close-window" => "windowId",
        "launch" => "executable arguments lifetime workingDirectory verb waitWindow",
        "open" => "target arguments verb workingDirectory waitWindow",
        "terminate-owned" => "processIds",
        "inspect" => "windowId format maxDepth rootElementId rootSnapshotToken interactiveOnly includeOffscreen includeBounds treeView",
        "find-element" => "windowId selector selectors searchRootElementId searchRootSnapshotToken includePath completeness treeView errorContext",
        "capabilities" => "windowId selector elementId snapshotToken elements includeDescriptors completeness treeView errorContext",
        "wait-condition" => "windowId selector condition expected anyOf pollMilliseconds searchRootElementId searchRootSnapshotToken completeness treeView",
        "find-item" => Element + " itemName",
        "read-grid" => Element + " startRow rowCount startColumn columnCount",
        "read-text" => Element + " selectionOnly maxLength",
        "generate-selector" => Element,
        "capture" => "windowId output region elementId snapshotToken scale",
        "clipboard-get" => "",
        "clipboard-set" => "text",
        "set-value" => Element + " value expectedValueHash" + After,
        "set-range-value" => Element + " value" + After,
        "set-toggle" => Element + " desiredState" + After,
        "set-expanded" => Element + " expanded" + After,
        "scroll-container" => Element + " horizontal vertical" + After,
        "invoke" or "select-item" or "set-focus" or "legacy-default-action" or "realize-item" or "scroll-item-into-view" => Element + After,
        "click" => Element + " position button count modifiers" + After,
        "type-text" => "windowId text" + After,
        "key" => "windowId key action" + After,
        "chord" => "windowId keys" + After,
        "move" => "windowId position",
        "wheel" => "windowId position delta",
        "drag" => "windowId start end button steps captureId",
        "release-input" => "",
        _ => throw new GuiException("UnsupportedOperation")
    };
    /// <summary>Every argument error names the problem and repeats the expected arguments of the operation.</summary>
    public static void Validate(string operation, JsonElement a)
    {
        try { Check(operation, a); }
        catch (GuiException e) when (e.Code != "UnsupportedOperation" && !e.Message.Contains(" | expected ", StringComparison.Ordinal))
        { throw new GuiException(e.Code, e.Message + " | expected " + OperationHelp.For(operation), e.Details); }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new GuiException("InvalidRequest", (e is KeyNotFoundException ? "A required argument is missing" : "An argument has the wrong JSON type") + " | expected " + OperationHelp.For(operation)); }
    }
    private static void Check(string operation, JsonElement a)
    {
        if (a.ValueKind != JsonValueKind.Object) throw new GuiException("InvalidRequest", "ArgumentsMustBeObject");
        GuiProtocol.Fields(a, Allowed(operation));
        var d = Operations.Get(operation);
        string T(string name) => GuiProtocol.Text(a, name);
        if (d.UsesWindow) _ = T("windowId");
        if (GuiProtocol.Has(a, "treeView") && a.GetProperty("treeView").GetString() is not ("control" or "raw" or "content")) throw new GuiException("InvalidTreeView");
        if (GuiProtocol.Has(a, "completeness") && a.GetProperty("completeness").GetString() is not ("default" or "realized")) throw new GuiException("InvalidCompleteness");
        Pair(a, "elementId", "snapshotToken"); Pair(a, "searchRootElementId", "searchRootSnapshotToken"); Pair(a, "rootElementId", "rootSnapshotToken");
        if (GuiProtocol.Has(a, "pollMilliseconds") && a.GetProperty("pollMilliseconds").GetInt32() is < 25 or > 1000) throw new GuiException("InvalidPollInterval");
        if (d.Category == "uia" && operation != "clipboard-set") { _ = T("elementId"); _ = T("snapshotToken"); }
        if (GuiProtocol.Has(a, "expect")) Expect(a.GetProperty("expect"));
        if (GuiProtocol.Has(a, "observeAfter")) ObserveAfter(a.GetProperty("observeAfter"));
        if (GuiProtocol.Has(a, "errorContext")) _ = a.GetProperty("errorContext").GetBoolean();
        switch (operation)
        {
            case "windows":
                if (GuiProtocol.Has(a, "includePopups")) _ = a.GetProperty("includePopups").GetBoolean();
                Kind(a); break;
            case "find-window":
            case "wait-window":
                if (!new[] { "title", "titleContains", "titleRegex", "className", "ownerWindowId", "processName", "kind" }.Any(n => GuiProtocol.Has(a, n))) throw new GuiException("InvalidWindowSelector");
                foreach (var f in new[] { "title", "titleContains", "titleRegex", "className", "ownerWindowId", "processName" }) if (GuiProtocol.Has(a, f)) _ = T(f);
                if (GuiProtocol.Has(a, "titleRegex")) GuiProtocol.SafeRegex(T("titleRegex"));
                Kind(a); break;
            case "window-state": if (T("state") is not ("minimize" or "maximize" or "restore" or "close")) throw new GuiException("InvalidWindowState"); break;
            case "launch":
                if (!Path.IsPathFullyQualified(T("executable"))) throw new GuiException("InvalidExecutable");
                if (GuiProtocol.Has(a, "workingDirectory") && !Path.IsPathFullyQualified(T("workingDirectory"))) throw new GuiException("InvalidWorkingDirectory");
                if (GuiProtocol.Has(a, "lifetime") && T("lifetime") is not ("keep" or "session")) throw new GuiException("InvalidLifetime");
                Arguments(a); Verb(a); WaitWindow(a); break;
            case "open":
                if (T("target").Contains('\0')) throw new GuiException("InvalidTarget");
                if (GuiProtocol.Has(a, "workingDirectory") && !Path.IsPathFullyQualified(T("workingDirectory"))) throw new GuiException("InvalidWorkingDirectory");
                Arguments(a); Verb(a); WaitWindow(a); break;
            case "terminate-owned":
                var ids = a.GetProperty("processIds").EnumerateArray().ToArray(); if (ids.Length is < 1 or > 128) throw new GuiException("InvalidProcesses");
                foreach (var value in ids) if (!GuiProtocol.Id(value.GetString())) throw new GuiException("InvalidProcesses"); break;
            case "inspect":
                if (GuiProtocol.Has(a, "format") && T("format") is not ("compact" or "json")) throw new GuiException("InvalidFormat");
                if (GuiProtocol.Has(a, "maxDepth") && a.GetProperty("maxDepth").GetInt32() is < 1 or > 16) throw new GuiException("InvalidDepth");
                foreach (var f in new[] { "interactiveOnly", "includeOffscreen", "includeBounds" }) if (GuiProtocol.Has(a, f)) _ = a.GetProperty(f).GetBoolean(); break;
            case "find-element":
                if (GuiProtocol.Has(a, "selector") == GuiProtocol.Has(a, "selectors")) throw new GuiException("InvalidSelector", "SelectorOrSelectorsRequired");
                if (GuiProtocol.Has(a, "selector")) Selector(a.GetProperty("selector"));
                else { var list = a.GetProperty("selectors").EnumerateArray().ToArray(); if (list.Length is < 1 or > 16) throw new GuiException("InvalidSelector", "SelectorsCount"); foreach (var s in list) Selector(s); }
                if (GuiProtocol.Has(a, "includePath")) _ = a.GetProperty("includePath").GetBoolean(); break;
            case "capabilities":
                var targets = new[] { "selector", "elementId", "elements" }.Count(n => GuiProtocol.Has(a, n));
                if (targets != 1) throw new GuiException("AmbiguousTarget", "OneOfSelectorElementIdElements");
                if (GuiProtocol.Has(a, "selector")) Selector(a.GetProperty("selector"));
                if (GuiProtocol.Has(a, "elements"))
                {
                    var elements = a.GetProperty("elements").EnumerateArray().ToArray(); if (elements.Length is < 1 or > 16) throw new GuiException("InvalidElements");
                    foreach (var e in elements) { GuiProtocol.Fields(e, "elementId snapshotToken"); _ = GuiProtocol.Text(e, "elementId"); _ = GuiProtocol.Text(e, "snapshotToken"); }
                }
                if (GuiProtocol.Has(a, "includeDescriptors")) _ = a.GetProperty("includeDescriptors").GetBoolean(); break;
            case "wait-condition":
                if (GuiProtocol.Has(a, "anyOf"))
                {
                    if (GuiProtocol.Has(a, "selector") || GuiProtocol.Has(a, "condition") || GuiProtocol.Has(a, "expected")) throw new GuiException("InvalidCondition", "AnyOfExcludesSelector");
                    var items = a.GetProperty("anyOf").EnumerateArray().ToArray(); if (items.Length is < 1 or > 4) throw new GuiException("InvalidCondition", "AnyOfCount");
                    foreach (var item in items) { GuiProtocol.Fields(item, "selector condition expected"); Selector(item.GetProperty("selector")); Condition(item); }
                }
                else { Selector(a.GetProperty("selector")); Condition(a); }
                break;
            case "find-item": _ = T("itemName"); break;
            case "read-grid":
                foreach (var f in new[] { "startRow", "startColumn" }) if (GuiProtocol.Has(a, f) && a.GetProperty(f).GetInt32() < 0) throw new GuiException("InvalidRange");
                foreach (var f in new[] { "rowCount", "columnCount" }) if (GuiProtocol.Has(a, f) && a.GetProperty(f).GetInt32() is < 1 or > 500) throw new GuiException("InvalidRange"); break;
            case "read-text":
                if (GuiProtocol.Has(a, "selectionOnly")) _ = a.GetProperty("selectionOnly").GetBoolean();
                if (GuiProtocol.Has(a, "maxLength") && a.GetProperty("maxLength").GetInt32() is < 1 or > 1_000_000) throw new GuiException("InvalidLength"); break;
            case "capture":
                if (GuiProtocol.Has(a, "output") && !Path.IsPathFullyQualified(T("output"))) throw new GuiException("InvalidOutputPath");
                if (GuiProtocol.Has(a, "region")) { var r = a.GetProperty("region"); GuiProtocol.Fields(r, "x y width height"); if (r.GetProperty("x").GetInt32() < 0 || r.GetProperty("y").GetInt32() < 0 || r.GetProperty("width").GetInt32() < 1 || r.GetProperty("height").GetInt32() < 1) throw new GuiException("InvalidRegion"); }
                if (GuiProtocol.Has(a, "region") && GuiProtocol.Has(a, "elementId")) throw new GuiException("AmbiguousTarget", "RegionOrElement");
                if (GuiProtocol.Has(a, "scale") && a.GetProperty("scale").GetDouble() is var scale && (!double.IsFinite(scale) || scale < 0.25 || scale > 1)) throw new GuiException("InvalidScale"); break;
            case "clipboard-set": Text(a.GetProperty("text").GetString(), 1_000_000); break;
            case "set-value":
                Text(a.GetProperty("value").GetString(), 65536);
                if (GuiProtocol.Has(a, "expectedValueHash") && T("expectedValueHash") is not { Length: 64 }) throw new GuiException("InvalidRequest", "expectedValueHash"); break;
            case "set-range-value": if (!double.IsFinite(a.GetProperty("value").GetDouble())) throw new GuiException("InvalidRangeValue"); break;
            case "set-toggle": if (T("desiredState") is not ("on" or "off" or "indeterminate")) throw new GuiException("InvalidToggleState"); break;
            case "set-expanded": _ = a.GetProperty("expanded").GetBoolean(); break;
            case "scroll-container": foreach (var field in new[] { "horizontal", "vertical" }) { var n = a.GetProperty(field).GetDouble(); if (!double.IsFinite(n) || n is < -1 or > 100) throw new GuiException("InvalidScrollPercent"); } break;
            case "click":
                var hasElement = GuiProtocol.Has(a, "elementId"); var hasPosition = GuiProtocol.Has(a, "position");
                if (hasElement == hasPosition) throw new GuiException("AmbiguousTarget", "ElementOrPosition");
                if (hasPosition) Position(a.GetProperty("position"));
                _ = GuiProtocol.Button(a);
                if (GuiProtocol.Has(a, "count") && a.GetProperty("count").GetInt32() is < 1 or > 3) throw new GuiException("InvalidCount");
                if (GuiProtocol.Has(a, "modifiers"))
                {
                    var modifiers = a.GetProperty("modifiers").EnumerateArray().Select(m => m.GetString()).ToArray();
                    if (modifiers.Length > 3 || modifiers.Distinct().Count() != modifiers.Length || modifiers.Any(m => m is not ("CTRL" or "SHIFT" or "ALT"))) throw new GuiException("InvalidModifiers");
                }
                break;
            case "type-text": var text = T("text"); Text(text, 4096); if (text.Any(c => char.IsControl(c) && c is not ('\n' or '\t'))) throw new GuiException("InvalidText"); break;
            case "key": _ = GuiProtocol.Key(T("key")); if (GuiProtocol.Has(a, "action") && T("action") is not ("press" or "down" or "up")) throw new GuiException("InvalidAction"); break;
            case "chord":
                var keys = a.GetProperty("keys").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
                if (keys.Length is < 1 or > 8 || keys.Distinct().Count() != keys.Length) throw new GuiException("InvalidKeys");
                foreach (var key in keys) _ = GuiProtocol.Key(key); break;
            case "move": Position(a.GetProperty("position")); break;
            case "wheel": Position(a.GetProperty("position")); if (a.GetProperty("delta").GetInt32() is < -1200 or > 1200 or 0) throw new GuiException("InvalidDelta"); break;
            case "drag":
                foreach (var f in new[] { "start", "end" }) { var p = a.GetProperty(f); GuiProtocol.Fields(p, "x y"); _ = p.GetProperty("x").GetInt32(); _ = p.GetProperty("y").GetInt32(); }
                if (GuiProtocol.Has(a, "captureId")) _ = T("captureId");
                _ = GuiProtocol.Button(a);
                if (GuiProtocol.Has(a, "steps") && a.GetProperty("steps").GetInt32() is < 1 or > 100) throw new GuiException("InvalidSteps"); break;
        }
    }
    private static void Pair(JsonElement a, string id, string token)
    {
        if (GuiProtocol.Has(a, id) != GuiProtocol.Has(a, token)) throw new GuiException("InvalidRequest", id + "/" + token + " must be given together");
        if (GuiProtocol.Has(a, id)) { _ = GuiProtocol.Text(a, id); _ = GuiProtocol.Text(a, token); }
    }
    private static void Kind(JsonElement a) { if (GuiProtocol.Has(a, "kind") && a.GetProperty("kind").GetString() is not ("main" or "owned" or "popup")) throw new GuiException("InvalidWindowKind"); }
    private static void Verb(JsonElement a) { if (GuiProtocol.Has(a, "verb") && a.GetProperty("verb").GetString() is not ("open" or "runas")) throw new GuiException("InvalidVerb"); }
    private static void Arguments(JsonElement a)
    {
        if (!GuiProtocol.Has(a, "arguments")) return;
        foreach (var arg in a.GetProperty("arguments").EnumerateArray()) if (arg.ValueKind != JsonValueKind.String || arg.GetString()!.Contains('\0')) throw new GuiException("InvalidArguments");
    }
    public static void WaitWindow(JsonElement a)
    {
        if (!GuiProtocol.Has(a, "waitWindow")) return;
        var w = a.GetProperty("waitWindow"); GuiProtocol.Fields(w, "timeoutMilliseconds title titleContains className");
        if (GuiProtocol.Has(w, "timeoutMilliseconds") && w.GetProperty("timeoutMilliseconds").GetInt32() is < 1 or > 30000) throw new GuiException("InvalidTimeout");
        foreach (var f in new[] { "title", "titleContains", "className" }) if (GuiProtocol.Has(w, f)) _ = GuiProtocol.Text(w, f);
    }
    private static void Position(JsonElement p) { GuiProtocol.Fields(p, "x y captureId"); _ = p.GetProperty("x").GetInt32(); _ = p.GetProperty("y").GetInt32(); if (GuiProtocol.Has(p, "captureId")) _ = GuiProtocol.Text(p, "captureId"); }
    private static void Text(string? value, int max)
    {
        if (value == null || value.Length > max || !GuiProtocol.ValidUnicode(value) || value.Contains('\0')) throw new GuiException("InvalidText");
    }
    public static void Selector(JsonElement selector, int depth = 0)
    {
        if (depth > 8) throw new GuiException("InvalidSelector", "SelectorTooDeep");
        GuiProtocol.Fields(selector, "automationId automationIdRegex controlType name nameContains nameStartsWith nameRegex className frameworkId ancestorAutomationId parent index");
        if (!selector.EnumerateObject().Any(p => p.Name != "index")) throw new GuiException("InvalidSelector", "EmptySelector");
        var names = new[] { "name", "nameContains", "nameStartsWith", "nameRegex" }.Count(n => GuiProtocol.Has(selector, n));
        if (names > 1) throw new GuiException("InvalidSelector", "OneNameOperator");
        if (GuiProtocol.Has(selector, "automationId") && GuiProtocol.Has(selector, "automationIdRegex")) throw new GuiException("InvalidSelector", "OneAutomationIdOperator");
        foreach (var p in selector.EnumerateObject())
        {
            if (p.Name == "parent") Selector(p.Value, depth + 1);
            else if (p.Name == "index") { if (depth > 0 || p.Value.GetInt32() is < 0 or > 10000) throw new GuiException("InvalidSelector", "InvalidIndex"); }
            else if (p.Value.ValueKind != JsonValueKind.String || p.Value.GetString() is not { Length: > 0 and <= 1024 }) throw new GuiException("InvalidSelector", "InvalidValue: " + p.Name);
            else if (p.Name is "nameRegex" or "automationIdRegex") GuiProtocol.SafeRegex(p.Value.GetString()!);
        }
    }
    public static void Condition(JsonElement a)
    {
        var condition = GuiProtocol.Text(a, "condition");
        if (!Conditions.Contains(condition)) throw new GuiException("InvalidCondition");
        var has = a.TryGetProperty("expected", out var e) && e.ValueKind != JsonValueKind.Null;
        switch (condition)
        {
            case "exists" or "absent" or "enabled" or "disabled" or "onscreen" or "offscreen":
                if (has) throw new GuiException("InvalidCondition", "ExpectedNotAllowed"); break;
            default:
                if (!has || e.ValueKind != JsonValueKind.String) throw new GuiException("InvalidCondition", "ExpectedStringRequired");
                var expected = e.GetString()!;
                if (condition is "text-contains" or "name-contains" && expected.Length == 0) throw new GuiException("InvalidCondition", "ExpectedEmpty");
                if (condition == "toggle-equals" && expected is not ("on" or "off" or "indeterminate")) throw new GuiException("InvalidCondition", "ToggleExpected");
                if (condition == "selected-equals" && expected is not ("true" or "false")) throw new GuiException("InvalidCondition", "SelectedExpected");
                if (condition == "expanded-equals" && expected is not ("expanded" or "collapsed" or "partially" or "leaf")) throw new GuiException("InvalidCondition", "ExpandedExpected");
                break;
        }
    }
    private static void Expect(JsonElement e)
    {
        GuiProtocol.Fields(e, "selector condition expected timeoutMilliseconds pollMilliseconds");
        Selector(e.GetProperty("selector")); Condition(e);
        if (GuiProtocol.Has(e, "timeoutMilliseconds") && e.GetProperty("timeoutMilliseconds").GetInt32() is < 1 or > 5000) throw new GuiException("InvalidTimeout");
    }
    private static void ObserveAfter(JsonElement o)
    {
        GuiProtocol.Fields(o, "rootElementId rootSnapshotToken settleMilliseconds maxDepth includeNewWindows");
        Pair(o, "rootElementId", "rootSnapshotToken");
        if (GuiProtocol.Has(o, "settleMilliseconds") && o.GetProperty("settleMilliseconds").GetInt32() is < 0 or > 1000) throw new GuiException("InvalidSettle");
        if (GuiProtocol.Has(o, "maxDepth") && o.GetProperty("maxDepth").GetInt32() is < 1 or > 16) throw new GuiException("InvalidDepth");
        if (GuiProtocol.Has(o, "includeNewWindows")) _ = o.GetProperty("includeNewWindows").GetBoolean();
    }
}

using System.Text.Json;
using System.Windows.Automation;

namespace DesktopTestDriver;

/// <summary>UIA work inside the per-session worker process. Every element is identified by its runtime id and a
/// snapshot token (hash of identity, parent and descriptive properties). A command revalidates, checks and
/// dispatches in one round trip.</summary>
public static class UiaAutomation
{
    /// <summary>Raised right before a provider method that can change the application is called.</summary>
    public static volatile bool DispatchStarted;
    public const int MaxNodes = 2048, MaxName = 1024, MaxValue = 65536;
    private static readonly Dictionary<string, AutomationElement> elements = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, (AutomationElement Element, ObservationNode Node, string ContainerToken)> virtualItems = new(StringComparer.Ordinal);
    public static void DisposeWorker() { elements.Clear(); virtualItems.Clear(); NativeAutomation.DisposeWorker(); }

    private static readonly (AutomationProperty Flag, string Name)[] PatternFlags = [
        (AutomationElement.IsInvokePatternAvailableProperty, "Invoke"), (AutomationElement.IsValuePatternAvailableProperty, "Value"),
        (AutomationElement.IsSelectionItemPatternAvailableProperty, "SelectionItem"), (AutomationElement.IsTogglePatternAvailableProperty, "Toggle"),
        (AutomationElement.IsExpandCollapsePatternAvailableProperty, "ExpandCollapse"), (AutomationElement.IsScrollPatternAvailableProperty, "Scroll"),
        (AutomationElement.IsScrollItemPatternAvailableProperty, "ScrollItem"), (AutomationElement.IsVirtualizedItemPatternAvailableProperty, "VirtualizedItem"),
        (AutomationElement.IsItemContainerPatternAvailableProperty, "ItemContainer"), (AutomationElement.IsGridPatternAvailableProperty, "Grid"),
        (AutomationElement.IsTablePatternAvailableProperty, "Table"), (AutomationElement.IsRangeValuePatternAvailableProperty, "RangeValue"),
        (AutomationElement.IsWindowPatternAvailableProperty, "Window"), (AutomationElement.IsTextPatternAvailableProperty, "Text"),
        (AutomationElement.IsSelectionPatternAvailableProperty, "Selection")
    ];
    private static readonly AutomationProperty? LegacyFlag = AutomationProperty.LookupById(30090);
    private static readonly AutomationProperty[] Basic = [
        AutomationElement.RuntimeIdProperty, AutomationElement.AutomationIdProperty, AutomationElement.ControlTypeProperty, AutomationElement.ClassNameProperty,
        AutomationElement.FrameworkIdProperty, AutomationElement.IsEnabledProperty, AutomationElement.IsOffscreenProperty, AutomationElement.IsPasswordProperty,
        AutomationElement.NameProperty, AutomationElement.BoundingRectangleProperty, AutomationElement.HasKeyboardFocusProperty, AutomationElement.HelpTextProperty,
        AutomationElement.ItemStatusProperty, ValuePattern.ValueProperty, ValuePattern.IsReadOnlyProperty, TogglePattern.ToggleStateProperty,
        SelectionItemPattern.IsSelectedProperty, ExpandCollapsePattern.ExpandCollapseStateProperty, RangeValuePattern.ValueProperty,
        RangeValuePattern.MinimumProperty, RangeValuePattern.MaximumProperty, RangeValuePattern.IsReadOnlyProperty
    ];
    private static Condition View(string view) => view switch { "raw" => Automation.RawViewCondition, "content" => Automation.ContentViewCondition, _ => Automation.ControlViewCondition };
    private static TreeWalker Walker(string view) => view switch { "raw" => TreeWalker.RawViewWalker, "content" => TreeWalker.ContentViewWalker, _ => TreeWalker.ControlViewWalker };
    private static CacheRequest Request(TreeScope scope, string view)
    {
        var cache = new CacheRequest { TreeScope = scope, TreeFilter = View(view), AutomationElementMode = AutomationElementMode.Full };
        foreach (var p in Basic) cache.Add(p);
        foreach (var p in PatternFlags) cache.Add(p.Flag);
        if (LegacyFlag != null) cache.Add(LegacyFlag);
        return cache;
    }
    private static CacheRequest IdOnly(string view)
    {
        var cache = new CacheRequest { TreeScope = TreeScope.Element, TreeFilter = View(view), AutomationElementMode = AutomationElementMode.Full };
        cache.Add(AutomationElement.RuntimeIdProperty); cache.Add(AutomationElement.IsItemContainerPatternAvailableProperty); cache.Add(AutomationElement.NameProperty); return cache;
    }
    private sealed record Context(UiaCommand Command, AutomationElement Root, string RootId, Native.Point Origin);
    private static string Rid(AutomationElement e, bool cached = true)
    {
        var value = cached ? e.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty) : e.GetRuntimeId();
        return value is int[] ids ? string.Join("-", ids) : throw new GuiException("StaleElement", "RuntimeIdUnavailable") { NoEffect = true };
    }
    private static int[] Ints(string runtimeId) => runtimeId.Split('-').Select(s => int.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    private static object P(AutomationElement e, AutomationProperty p) => e.GetCachedPropertyValue(p, true);
    public static string Token(DesktopWindow w, string runtimeId, string? parent, string automationId, string name, string controlType, string className, string frameworkId) =>
        GuiProtocol.Hash(string.Join('\u001f', w.Process.Pid, w.Process.CreatedAt.UtcTicks, w.Handle, w.Generation, runtimeId, parent ?? "", automationId, name, controlType, className, frameworkId));
    private static ObservationNode Node(Context ctx, AutomationElement e, string? parent)
    {
        var rid = Rid(e);
        var automationId = P(e, AutomationElement.AutomationIdProperty) as string ?? "";
        var type = P(e, AutomationElement.ControlTypeProperty) is ControlType ct ? ct.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal) : "Unknown";
        var className = P(e, AutomationElement.ClassNameProperty) as string ?? "";
        var framework = P(e, AutomationElement.FrameworkIdProperty) as string ?? "";
        var name = P(e, AutomationElement.NameProperty) as string ?? "";
        var password = P(e, AutomationElement.IsPasswordProperty) is true;
        var patterns = PatternFlags.Where(p => P(e, p.Flag) is true).Select(p => p.Name).ToList();
        if (LegacyFlag != null && P(e, LegacyFlag) is true) patterns.Add("LegacyIAccessible");
        ElementValue value = new(false, null, "unavailable");
        if (password) value = new(false, null, "password");
        else if (patterns.Contains("Value") && P(e, ValuePattern.ValueProperty) is string text) value = text.Length > MaxValue ? new(false, null, "tooLarge") : new(true, text, null, "value");
        var token = Token(ctx.Command.Window, rid, parent, automationId, name, type, className, framework);
        PixelRect? bounds = null;
        if (P(e, AutomationElement.BoundingRectangleProperty) is System.Windows.Rect r && !r.IsEmpty && r.Width > 0 && r.Height > 0)
            bounds = new((int)Math.Round(r.X) - ctx.Origin.X, (int)Math.Round(r.Y) - ctx.Origin.Y, (int)Math.Round(r.Width), (int)Math.Round(r.Height));
        string? Text(AutomationProperty property) => P(e, property) is string s && s.Length > 0 ? s : null;
        return new ObservationNode(rid, parent, automationId, name.Length > MaxName ? name[..MaxName] : name, type, className, framework,
            P(e, AutomationElement.IsEnabledProperty) is true, P(e, AutomationElement.IsOffscreenProperty) is true, value, patterns.ToArray(), token)
        {
            NameTruncated = name.Length > MaxName ? true : null,
            ToggleState = patterns.Contains("Toggle") && P(e, TogglePattern.ToggleStateProperty) is ToggleState ts ? ts.ToString().ToLowerInvariant() : null,
            Selected = patterns.Contains("SelectionItem") && P(e, SelectionItemPattern.IsSelectedProperty) is bool selected ? selected : null,
            ExpandState = patterns.Contains("ExpandCollapse") && P(e, ExpandCollapsePattern.ExpandCollapseStateProperty) is ExpandCollapseState es ? es switch { ExpandCollapseState.Expanded => "expanded", ExpandCollapseState.Collapsed => "collapsed", ExpandCollapseState.PartiallyExpanded => "partially", _ => "leaf" } : null,
            Focused = P(e, AutomationElement.HasKeyboardFocusProperty) is true ? true : null,
            RangeValue = patterns.Contains("RangeValue") && P(e, RangeValuePattern.ValueProperty) is double rv ? new(rv, P(e, RangeValuePattern.MinimumProperty) is double min ? min : 0, P(e, RangeValuePattern.MaximumProperty) is double max ? max : 0) : null,
            HelpText = Text(AutomationElement.HelpTextProperty), ItemStatus = Text(AutomationElement.ItemStatusProperty), Bounds = bounds
        };
    }
    private static void Keep(Context ctx, string rid, AutomationElement e)
    {
        if (elements.Count >= ElementRefs.Limit) foreach (var key in elements.Keys.Take(1024).ToArray()) elements.Remove(key);
        elements[ctx.Command.Window.Handle + "|" + rid] = e;
    }
    private static Context Open(UiaCommand c)
    {
        var h = new IntPtr(c.Window.Handle);
        if (!Native.IsWindow(h)) throw new GuiException("WindowNotFound", "WindowDestroyed") { NoEffect = true };
        Native.GetWindowThreadProcessId(h, out var pid);
        if (pid != c.Window.Process.Pid || Processes.Identify((int)pid) != c.Window.Process) throw new GuiException("WindowChanged") { NoEffect = true };
        var origin = new Native.Point(); Native.ClientToScreen(h, ref origin);
        var root = AutomationElement.FromHandle(h);
        return new(c, root, string.Join("-", root.GetRuntimeId()), origin);
    }
    /// <summary>Finds and revalidates a referenced element. Stale when the runtime id, parent or descriptive properties changed.</summary>
    private static (AutomationElement Element, ObservationNode Node) Resolve(Context ctx, ElementLocator locator)
    {
        if (locator.RuntimeId.StartsWith("virtual-", StringComparison.Ordinal))
        {
            if (!virtualItems.TryGetValue(locator.RuntimeId, out var item) || item.Node.SnapshotToken != locator.Token) throw new GuiException("StaleElement", "VirtualItemUnknown") { NoEffect = true };
            return (item.Element, item.Node);
        }
        try
        {
            var key = ctx.Command.Window.Handle + "|" + locator.RuntimeId;
            AutomationElement? element;
            if (locator.RuntimeId == ctx.RootId) element = ctx.Root;
            else if (!elements.TryGetValue(key, out element))
            {
                using (IdOnly("raw").Activate()) element = ctx.Root.FindFirst(TreeScope.Subtree, new PropertyCondition(AutomationElement.RuntimeIdProperty, Ints(locator.RuntimeId)));
                if (element == null) throw new GuiException("StaleElement", "ElementGone") { NoEffect = true };
            }
            var refreshed = element.GetUpdatedCache(Request(TreeScope.Element, locator.TreeView));
            if (Rid(refreshed) != locator.RuntimeId) throw new GuiException("StaleElement", "RuntimeIdChanged") { NoEffect = true };
            string? parent = null;
            if (locator.RuntimeId != ctx.RootId)
            {
                var up = Walker(locator.TreeView).GetParent(refreshed, IdOnly(locator.TreeView));
                parent = up == null ? null : Rid(up);
            }
            var node = Node(ctx, refreshed, parent);
            if (node.SnapshotToken != locator.Token) throw new GuiException("StaleElement", "ElementChanged") { NoEffect = true };
            Keep(ctx, locator.RuntimeId, refreshed);
            return (refreshed, node);
        }
        catch (ElementNotAvailableException) { throw new GuiException("StaleElement", "ElementNotAvailable") { NoEffect = true }; }
    }
    private static ObservationNode Details(Context ctx, AutomationElement element, ObservationNode node)
    {
        if (node.Value.Available || node.Value.Reason == "password" || node.ElementId.StartsWith("virtual-", StringComparison.Ordinal)) return node;
        var legacy = NativeAutomation.Legacy(ctx.Command.Window.Handle, Ints(node.ElementId));
        var patterns = legacy.Available && !node.Patterns.Contains("LegacyIAccessible") ? node.Patterns.Append("LegacyIAccessible").ToArray() : node.Patterns;
        if (legacy.Available && legacy.Value is { Length: > 0 } text) return node with { Value = new(true, text.Length > MaxValue ? text[..MaxValue] : text, null, "legacy"), Patterns = patterns };
        return node with { Patterns = patterns };
    }
    public static UiaResult Execute(UiaCommand c)
    {
        var ctx = Open(c);
        try
        {
            return c.Operation switch
            {
                "inspect" => Inspect(ctx),
                "find-element" => FindElements(ctx),
                "wait-condition" => FindElements(ctx),
                "capabilities" => Capabilities(ctx),
                "find-item" => FindItem(ctx),
                "read-grid" => ReadGrid(ctx),
                "read-text" => ReadText(ctx),
                "generate-selector" => GenerateSelector(ctx),
                "hit-test" => HitTest(ctx),
                "window-state" => WindowState(ctx),
                _ when Operations.ElementMutation(c.Operation) => Mutate(ctx),
                _ => throw new GuiException("UnsupportedOperation") { NoEffect = true }
            };
        }
        catch (ElementNotAvailableException) { throw new GuiException("StaleElement", "ElementNotAvailable") { NoEffect = !DispatchStarted }; }
    }
    // ---------------------------------------------------------------- observation
    private static UiaResult Inspect(Context ctx)
    {
        var a = ctx.Command.Arguments; var view = ctx.Command.TreeView;
        var maxDepth = GuiProtocol.OptionalInt(a, "maxDepth") ?? 16;
        AutomationElement root = ctx.Root; string? rootParent = null;
        if (ctx.Command.SearchRoot != null) { var resolved = Resolve(ctx, ctx.Command.SearchRoot); root = resolved.Element; rootParent = resolved.Node.ParentId; }
        // One cross-process call fetches the whole subtree with every property needed.
        var cached = root.GetUpdatedCache(Request(TreeScope.Subtree, view));
        var nodes = new List<ObservationNode>(); var depths = new Dictionary<string, int>();
        var queue = new Queue<(AutomationElement Element, string? Parent, int Depth)>(); queue.Enqueue((cached, rootParent, 0));
        string? truncation = null; var errors = 0;
        while (queue.Count > 0)
        {
            if (nodes.Count >= MaxNodes) { truncation = "node-limit"; break; }
            var (element, parent, depth) = queue.Dequeue();
            try
            {
                var node = Node(ctx, element, parent); nodes.Add(node); depths[node.ElementId] = depth; Keep(ctx, node.ElementId, element);
                var children = element.CachedChildren;
                if (children.Count == 0) continue;
                if (depth + 1 > maxDepth) { truncation ??= "depth-limit"; continue; }
                foreach (AutomationElement child in children) queue.Enqueue((child, node.ElementId, depth + 1));
            }
            catch (Exception e) when (e is ElementNotAvailableException or System.Runtime.InteropServices.COMException or InvalidOperationException) { errors++; }
        }
        // Breadth-first reading, depth-first output: every child line follows its parent.
        var byParent = nodes.Skip(1).GroupBy(n => n.ParentId!).ToDictionary(g => g.Key, g => g.ToList());
        var ordered = new List<ObservationNode>(nodes.Count); var stack = new Stack<ObservationNode>(); if (nodes.Count > 0) stack.Push(nodes[0]);
        while (stack.Count > 0)
        {
            var n = stack.Pop(); ordered.Add(n);
            if (byParent.TryGetValue(n.ElementId, out var list)) for (int i = list.Count - 1; i >= 0; i--) stack.Push(list[i]);
        }
        nodes = ordered;
        IEnumerable<ObservationNode> kept = nodes;
        var interactiveOnly = GuiProtocol.Flag(a, "interactiveOnly"); var includeOffscreen = GuiProtocol.Flag(a, "includeOffscreen", true);
        if (interactiveOnly || !includeOffscreen)
        {
            var byId = nodes.ToDictionary(n => n.ElementId); var keep = new HashSet<string>();
            foreach (var n in nodes)
            {
                var wanted = (includeOffscreen || !n.Offscreen) && (!interactiveOnly || n.Name.Length > 0 || n.Patterns.Any(p => p is not ("Text" or "Window" or "Selection" or "LegacyIAccessible")));
                if (!wanted) continue;
                for (string? id = n.ElementId; id != null && keep.Add(id); id = byId.GetValueOrDefault(id)?.ParentId) { }
            }
            keep.Add(nodes[0].ElementId);
            kept = nodes.Where(n => keep.Contains(n.ElementId));
        }
        var containers = nodes.Where(n => n.Patterns.Contains("ItemContainer")).ToArray();
        return new UiaResult(kept.ToArray()) { Complete = truncation == null && errors == 0 && containers.Length == 0, TruncationReason = truncation ?? (errors > 0 ? "provider-errors" : containers.Length > 0 ? "virtualized-items-not-enumerated" : null), VirtualizedContainers = containers.Length > 0 ? containers : null };
    }
    private static ControlType ControlTypeOf(string name) =>
        controlTypes.TryGetValue(name, out var type) ? type : throw new GuiException("InvalidSelector", "UnknownControlType: " + name) { NoEffect = true };
    private static readonly Dictionary<string, ControlType> controlTypes = typeof(ControlType).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.FieldType == typeof(ControlType)).ToDictionary(f => f.Name, f => (ControlType)f.GetValue(null)!, StringComparer.Ordinal);
    private sealed class Search(Context ctx, AutomationElement root, string view)
    {
        public readonly Context Ctx = ctx; public readonly AutomationElement Root = root; public readonly string View = view;
        private readonly Dictionary<string, (AutomationElement Element, ObservationNode Node)?> parents = new();
        private AutomationElement[]? containers;
        public AutomationElement[] Containers()
        {
            if (containers != null) return containers;
            using (IdOnly("raw").Activate())
                containers = Root.FindAll(TreeScope.Subtree, new PropertyCondition(AutomationElement.IsItemContainerPatternAvailableProperty, true)).Cast<AutomationElement>().ToArray();
            return containers;
        }
        public (AutomationElement Element, ObservationNode Node)? Parent(AutomationElement element, ObservationNode node)
        {
            if (node.ParentId == null) return null;
            if (parents.TryGetValue(node.ElementId, out var known)) return known;
            var up = Walker(View).GetParent(element, Request(TreeScope.Element, View));
            (AutomationElement, ObservationNode)? result = null;
            if (up != null)
            {
                var upId = Rid(up);
                string? grand = upId == Ctx.RootId ? null : Walker(View).GetParent(up, IdOnly(View)) is { } g ? Rid(g) : null;
                result = (up, Node(Ctx, up, grand));
            }
            parents[node.ElementId] = result; return result;
        }
        /// <summary>True when an ancestor up to the window root exposes ItemContainer (a virtualized list, grid, combo box and so on).</summary>
        public bool InsideContainer(AutomationElement element)
        {
            var cursor = element;
            for (int i = 0; i < 64; i++)
            {
                var up = TreeWalker.RawViewWalker.GetParent(cursor, IdOnly("raw")); if (up == null) return false;
                if (up.GetCachedPropertyValue(AutomationElement.IsItemContainerPatternAvailableProperty) is true) return true;
                if (Rid(up) == Ctx.RootId) return false;
                cursor = up;
            }
            return true;
        }
    }
    private static string? ParentOf(Search s, AutomationElement e, string rid)
    {
        if (rid == s.Ctx.RootId) return null;
        var up = Walker(s.View).GetParent(e, IdOnly(s.View)); return up == null ? null : Rid(up);
    }
    private static bool Matches(Search s, AutomationElement element, ObservationNode node, ElementSelector selector, int depth = 0)
    {
        if (depth > 8) return false;
        if (selector.AutomationId != null && node.AutomationId != selector.AutomationId) return false;
        if (selector.AutomationIdRegex != null && !GuiProtocol.SafeRegex(selector.AutomationIdRegex).IsMatch(node.AutomationId)) return false;
        if (selector.ControlType != null && node.ControlType != selector.ControlType) return false;
        if (selector.Name != null && node.Name != selector.Name) return false;
        if (selector.NameContains != null && !node.Name.Contains(selector.NameContains, StringComparison.Ordinal)) return false;
        if (selector.NameStartsWith != null && !node.Name.StartsWith(selector.NameStartsWith, StringComparison.Ordinal)) return false;
        if (selector.NameRegex != null && !GuiProtocol.SafeRegex(selector.NameRegex).IsMatch(node.Name)) return false;
        if (selector.ClassName != null && node.ClassName != selector.ClassName) return false;
        if (selector.FrameworkId != null && node.FrameworkId != selector.FrameworkId) return false;
        if (selector.Parent != null)
        {
            var parent = s.Parent(element, node);
            if (parent == null || !Matches(s, parent.Value.Element, parent.Value.Node, selector.Parent, depth + 1)) return false;
        }
        if (selector.AncestorAutomationId != null)
        {
            var cursor = s.Parent(element, node); var found = false;
            for (int i = 0; cursor != null && i < 64; i++)
            {
                if (cursor.Value.Node.AutomationId == selector.AncestorAutomationId) { found = true; break; }
                cursor = s.Parent(cursor.Value.Element, cursor.Value.Node);
            }
            if (!found) return false;
        }
        return true;
    }
    /// <summary>Selector search with the virtualization rule: outside virtualized containers an automationId match is
    /// accepted as unique; anything else inside a window with containers needs completeness "realized".</summary>
    private static SelectorResult Find(Search s, ElementSelector selector, string completeness, bool nearest)
    {
        var conditions = new List<Condition> { View(s.View) };
        if (selector.AutomationId != null) conditions.Add(new PropertyCondition(AutomationElement.AutomationIdProperty, selector.AutomationId));
        if (selector.Name != null) conditions.Add(new PropertyCondition(AutomationElement.NameProperty, selector.Name));
        if (selector.ControlType != null) conditions.Add(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlTypeOf(selector.ControlType)));
        if (selector.ClassName != null) conditions.Add(new PropertyCondition(AutomationElement.ClassNameProperty, selector.ClassName));
        if (selector.FrameworkId != null) conditions.Add(new PropertyCondition(AutomationElement.FrameworkIdProperty, selector.FrameworkId));
        AutomationElementCollection found;
        using (Request(TreeScope.Element, s.View).Activate())
            found = s.Root.FindAll(TreeScope.Subtree, conditions.Count == 1 ? conditions[0] : new AndCondition(conditions.ToArray()));
        if (found.Count > 4096) return new(null, new("SearchIncomplete", "TooManyCandidates"));
        var matches = new List<(AutomationElement Element, ObservationNode Node)>();
        foreach (AutomationElement element in found)
        {
            try
            {
                var rid = Rid(element);
                var node = Node(s.Ctx, element, ParentOf(s, element, rid));
                if (Matches(s, element, node, selector)) { matches.Add((element, node)); Keep(s.Ctx, rid, element); }
            }
            catch (ElementNotAvailableException) { }
        }
        var hasContainers = s.Containers().Length > 0;
        string scope;
        if (completeness == "realized") scope = "realized-only";
        else if (!hasContainers) scope = "complete";
        else if (selector.AutomationId != null && matches.Count == 1 && selector.Index == null && !s.InsideContainer(matches[0].Element)) scope = "outside-virtualized";
        else if (selector.AutomationId != null && matches.Count == 0) return new(null, new("SearchIncomplete", "NotFoundOutsideVirtualized", new { virtualizedContainers = s.Containers().Length }));
        else return new(null, new("SearchIncomplete", "VirtualizedContainerPresent", new { virtualizedContainers = s.Containers().Length, hint = "use completeness: realized, or an automationId outside the container" }), matches.Count);
        if (matches.Count == 0)
            return new(null, new("ElementNotFound", "ElementNotFound", nearest ? new { nearest = Nearest(s, selector) } : null), 0, scope);
        if (selector.Index is { } index)
        {
            if (index >= matches.Count) return new(null, new("ElementNotFound", "IndexOutOfRange", new { matchCount = matches.Count }), matches.Count, scope);
            var chosen = matches[index];
            return new(Details(s.Ctx, chosen.Element, chosen.Node), null, matches.Count, scope) { Path = PathOf(s, chosen.Element, chosen.Node) };
        }
        if (matches.Count > 1)
        {
            var candidates = matches.Take(5).Select(m => m.Node).ToArray();
            var parentNames = matches.Take(5).Select(m => s.Parent(m.Element, m.Node)?.Node.Name ?? "").ToArray();
            return new(null, new("AmbiguousTarget", "AmbiguousTarget", new { matchCount = matches.Count, candidates, parentNames }), matches.Count, scope);
        }
        return new(Details(s.Ctx, matches[0].Element, matches[0].Node), null, 1, scope) { Path = PathOf(s, matches[0].Element, matches[0].Node) };
    }
    private static ObservationNode[] PathOf(Search s, AutomationElement element, ObservationNode node)
    {
        var path = new List<ObservationNode>(); var cursor = s.Parent(element, node);
        for (int i = 0; cursor != null && i < 32; i++) { path.Add(cursor.Value.Node); cursor = s.Parent(cursor.Value.Element, cursor.Value.Node); }
        path.Reverse(); return path.ToArray();
    }
    private static object[] Nearest(Search s, ElementSelector selector)
    {
        var wanted = (selector.AutomationId ?? selector.AutomationIdRegex ?? selector.Name ?? selector.NameContains ?? selector.NameStartsWith ?? selector.NameRegex ?? "").ToLowerInvariant();
        if (wanted.Length == 0) return [];
        AutomationElementCollection all;
        var cache = new CacheRequest { TreeScope = TreeScope.Element, TreeFilter = View(s.View) };
        cache.Add(AutomationElement.AutomationIdProperty); cache.Add(AutomationElement.NameProperty); cache.Add(AutomationElement.ControlTypeProperty);
        using (cache.Activate()) all = s.Root.FindAll(TreeScope.Subtree, View(s.View));
        return all.Cast<AutomationElement>().Take(MaxNodes).Select(e =>
        {
            var id = e.GetCachedPropertyValue(AutomationElement.AutomationIdProperty) as string ?? ""; var name = e.GetCachedPropertyValue(AutomationElement.NameProperty) as string ?? "";
            var type = e.GetCachedPropertyValue(AutomationElement.ControlTypeProperty) is ControlType ct ? ct.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal) : "";
            return (Score: Math.Max(Similarity(wanted, id.ToLowerInvariant()), Similarity(wanted, name.ToLowerInvariant())), Value: (object)new { automationId = id, name = name.Length > 80 ? name[..80] : name, controlType = type });
        }).Where(x => x.Score > 0.3).OrderByDescending(x => x.Score).Take(3).Select(x => x.Value).ToArray();
    }
    private static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1; if (b.Contains(a) || a.Contains(b)) return 0.8;
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i; for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++) for (int j = 1; j <= b.Length; j++) d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return 1 - (double)d[a.Length, b.Length] / Math.Max(a.Length, b.Length);
    }
    private static Search NewSearch(Context ctx)
    {
        var root = ctx.Root;
        if (ctx.Command.SearchRoot != null) root = Resolve(ctx, ctx.Command.SearchRoot).Element;
        return new(ctx, root, ctx.Command.TreeView);
    }
    private static UiaResult FindElements(Context ctx)
    {
        var s = NewSearch(ctx); var nearest = ctx.Command.Operation == "find-element";
        var selectors = ctx.Command.Selectors ?? [ctx.Command.Selector ?? throw new GuiException("InvalidSelector") { NoEffect = true }];
        var results = selectors.Select(selector => Find(s, selector, ctx.Command.Completeness, nearest)).ToArray();
        var containers = ctx.Command.Completeness == "realized" && s.Containers().Length > 0
            ? s.Containers().Take(16).Select(e => { var rid = Rid(e); Keep(ctx, rid, e); var full = e.GetUpdatedCache(Request(TreeScope.Element, s.View)); return Node(ctx, full, ParentOf(s, full, rid)); }).ToArray() : null;
        return new UiaResult([], results[0].Target) { Results = results, MatchCount = results[0].MatchCount, UniquenessScope = results[0].UniquenessScope, VirtualizedContainers = containers };
    }
    private static UiaResult Capabilities(Context ctx)
    {
        if (ctx.Command.Elements is { } list)
        {
            var results = list.Select(l =>
            {
                try { var r = Resolve(ctx, l); return new SelectorResult(Details(ctx, r.Element, r.Node), null, 1); }
                catch (GuiException e) { return new SelectorResult(null, new(e.Code, e.Message, e.Details)); }
            }).ToArray();
            return new UiaResult([], results[0].Target) { Results = results };
        }
        if (ctx.Command.Target != null) { var r = Resolve(ctx, ctx.Command.Target); return new UiaResult([], Details(ctx, r.Element, r.Node)); }
        var found = Find(NewSearch(ctx), ctx.Command.Selector!, ctx.Command.Completeness, true);
        if (found.Error != null) throw new GuiException(found.Error.Code, found.Error.Message, found.Error.Data) { NoEffect = true };
        return new UiaResult([], found.Target) { MatchCount = found.MatchCount, UniquenessScope = found.UniquenessScope };
    }
    private static UiaResult FindItem(Context ctx)
    {
        var (container, node) = Resolve(ctx, ctx.Command.Target!);
        if (!container.TryGetCurrentPattern(ItemContainerPattern.Pattern, out var pattern)) throw new GuiException("PatternNotSupported") { NoEffect = true };
        var itemName = GuiProtocol.Text(ctx.Command.Arguments, "itemName");
        var items = (ItemContainerPattern)pattern;
        var item = items.FindItemByProperty(null, AutomationElement.NameProperty, itemName) ?? throw new GuiException("ElementNotFound") { NoEffect = true };
        if (items.FindItemByProperty(item, AutomationElement.NameProperty, itemName) != null) throw new GuiException("AmbiguousTarget") { NoEffect = true };
        var id = "virtual-" + Guid.NewGuid().ToString("N")[..16];
        var supported = new List<string>();
        foreach (var (pattern2, name) in new[] { (SelectionItemPattern.Pattern, "SelectionItem"), (ScrollItemPattern.Pattern, "ScrollItem"), (VirtualizedItemPattern.Pattern, "VirtualizedItem"), (InvokePattern.Pattern, "Invoke"), (TogglePattern.Pattern, "Toggle"), (ExpandCollapsePattern.Pattern, "ExpandCollapse") })
            if (item.TryGetCurrentPattern(pattern2, out _)) supported.Add(name);
        var token = GuiProtocol.Hash(node.SnapshotToken + "|" + id + "|" + itemName);
        var found = new ObservationNode(id, node.ElementId, "", itemName, "ListItem", "", "", true, true, new(false, null, "unrealized"), supported.ToArray(), token);
        if (virtualItems.Count >= 1024) foreach (var key in virtualItems.Keys.Take(256).ToArray()) virtualItems.Remove(key);
        virtualItems[id] = (item, found, node.SnapshotToken);
        return new UiaResult([node, found], found);
    }
    private static UiaResult ReadGrid(Context ctx)
    {
        var (element, node) = Resolve(ctx, ctx.Command.Target!);
        if (!element.TryGetCurrentPattern(GridPattern.Pattern, out var g)) throw new GuiException("PatternNotSupported") { NoEffect = true };
        var grid = (GridPattern)g; var a = ctx.Command.Arguments;
        int rows = grid.Current.RowCount, columns = grid.Current.ColumnCount;
        int startRow = GuiProtocol.OptionalInt(a, "startRow") ?? 0, startColumn = GuiProtocol.OptionalInt(a, "startColumn") ?? 0;
        int rowCount = Math.Min(GuiProtocol.OptionalInt(a, "rowCount") ?? 50, Math.Max(0, rows - startRow));
        int columnCount = Math.Min(GuiProtocol.OptionalInt(a, "columnCount") ?? columns, Math.Max(0, columns - startColumn));
        if ((long)rowCount * columnCount > 500) rowCount = Math.Max(1, 500 / Math.Max(1, columnCount));
        string[]? headers = null;
        if (element.TryGetCurrentPattern(TablePattern.Pattern, out var t))
            try { headers = ((TablePattern)t).Current.GetColumnHeaders().Select(h => h.Current.Name).Skip(startColumn).Take(columnCount).ToArray(); } catch (Exception e) when (e is InvalidOperationException or ElementNotAvailableException) { }
        var cells = new List<string?[]>();
        for (int r = startRow; r < startRow + rowCount; r++)
        {
            var row = new string?[columnCount];
            for (int col = 0; col < columnCount; col++)
            {
                try
                {
                    var cell = grid.GetItem(r, startColumn + col);
                    row[col] = cell.TryGetCurrentPattern(ValuePattern.Pattern, out var v) ? ((ValuePattern)v).Current.Value : cell.Current.Name;
                }
                catch (Exception e) when (e is InvalidOperationException or ElementNotAvailableException or ArgumentException) { row[col] = null; }
            }
            cells.Add(row);
        }
        return new UiaResult([], node) { Data = JsonSerializer.SerializeToElement(new { rowCount = rows, columnCount = columns, startRow, startColumn, columnHeaders = headers, rows = cells }, GuiProtocol.Json) };
    }
    private static UiaResult ReadText(Context ctx)
    {
        var (element, node) = Resolve(ctx, ctx.Command.Target!); var a = ctx.Command.Arguments;
        var max = GuiProtocol.OptionalInt(a, "maxLength") ?? MaxValue;
        string text; string source;
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var p))
        {
            var pattern = (TextPattern)p;
            if (GuiProtocol.Flag(a, "selectionOnly")) text = string.Join("\n", pattern.GetSelection().Select(r => r.GetText(max)));
            else text = pattern.DocumentRange.GetText(max + 1);
            source = "text";
        }
        else if (node.Value.Available) { text = node.Value.Text ?? ""; source = "value"; }
        else { var detailed = Details(ctx, element, node); if (!detailed.Value.Available) throw new GuiException("PatternNotSupported", "NoTextOrValue") { NoEffect = true }; text = detailed.Value.Text ?? ""; source = detailed.Value.Source ?? "legacy"; }
        var truncated = text.Length > max; if (truncated) text = text[..max];
        return new UiaResult([], node) { Data = JsonSerializer.SerializeToElement(new { text, source, truncated }, GuiProtocol.Json) };
    }
    /// <summary>Shortest selector that matches exactly this element in the window, plus alternatives. Each candidate is checked.</summary>
    private static UiaResult GenerateSelector(Context ctx)
    {
        var (element, node) = Resolve(ctx, ctx.Command.Target!);
        var s = new Search(ctx, ctx.Root, ctx.Command.Target!.TreeView);
        var parent = s.Parent(element, node)?.Node;
        var candidates = new List<ElementSelector>();
        if (node.AutomationId.Length > 0) candidates.Add(new(AutomationId: node.AutomationId));
        if (node.Name.Length > 0) candidates.Add(new(ControlType: node.ControlType, Name: node.Name));
        if (node.AutomationId.Length > 0) candidates.Add(new(AutomationId: node.AutomationId, ControlType: node.ControlType));
        if (parent != null)
        {
            var parentSelector = parent.AutomationId.Length > 0 ? new ElementSelector(AutomationId: parent.AutomationId) : parent.Name.Length > 0 ? new ElementSelector(ControlType: parent.ControlType, Name: parent.Name) : null;
            if (parentSelector != null)
            {
                if (node.Name.Length > 0) candidates.Add(new(ControlType: node.ControlType, Name: node.Name, Parent: parentSelector));
                candidates.Add(new(ControlType: node.ControlType, Parent: parentSelector));
            }
        }
        if (node.Name.Length > 0) candidates.Add(new(ControlType: node.ControlType, Name: node.Name, Index: 0));
        candidates.Add(new(ControlType: node.ControlType, Index: 0));
        var unique = new List<(ElementSelector Selector, string? Scope)>();
        foreach (var candidate in candidates)
        {
            if (candidate.Index != null)
            {
                var indexed = FindIndex(s, candidate, node.ElementId);
                if (indexed != null) unique.Add((candidate with { Index = indexed }, "indexed"));
                continue;
            }
            var result = Find(s, candidate, "default", false);
            if (result.Target?.ElementId == node.ElementId && !unique.Any(u => u.Selector == candidate)) unique.Add((candidate, result.UniquenessScope));
        }
        if (unique.Count == 0) throw new GuiException("SelectorUnavailable", "NoStableSelector") { NoEffect = true };
        return new UiaResult([], node) { Data = JsonSerializer.SerializeToElement(new { selector = unique[0].Selector, uniquenessScope = unique[0].Scope, alternatives = unique.Skip(1).Select(u => new { selector = u.Selector, uniquenessScope = u.Scope }).ToArray() }, GuiProtocol.Json) };
    }
    private static int? FindIndex(Search s, ElementSelector selector, string runtimeId)
    {
        var conditions = new List<Condition> { View(s.View), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlTypeOf(selector.ControlType!)) };
        if (selector.Name != null) conditions.Add(new PropertyCondition(AutomationElement.NameProperty, selector.Name));
        if (s.Containers().Length > 0) return null;
        AutomationElementCollection found;
        using (IdOnly(s.View).Activate()) found = s.Root.FindAll(TreeScope.Subtree, new AndCondition(conditions.ToArray()));
        for (int i = 0; i < found.Count; i++) if (Rid(found[i]) == runtimeId) return i;
        return null;
    }
    private static UiaResult HitTest(Context ctx)
    {
        var point = ctx.Command.HitPoint!;
        var hit = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
        var target = ctx.Command.Target!.RuntimeId;
        var cursor = hit; var inside = false;
        for (int i = 0; cursor != null && i < 64; i++)
        {
            if (string.Join("-", cursor.GetRuntimeId()) == target) { inside = true; break; }
            cursor = TreeWalker.RawViewWalker.GetParent(cursor);
        }
        object? blockedBy = null;
        if (!inside)
        {
            var top = Native.GetAncestor(Native.WindowFromPoint(new Native.Point { X = point.X, Y = point.Y }), 2);
            var title = new System.Text.StringBuilder(512); Native.GetWindowText(top, title, title.Capacity);
            blockedBy = new { name = hit.Current.Name, controlType = hit.Current.ControlType.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal), windowTitle = title.ToString() };
        }
        return new UiaResult([]) { Data = JsonSerializer.SerializeToElement(new { inside, blockedBy }, GuiProtocol.Json) };
    }
    private static UiaResult WindowState(Context ctx)
    {
        var state = GuiProtocol.Text(ctx.Command.Arguments, "state");
        if (ctx.Root.TryGetCurrentPattern(WindowPattern.Pattern, out var p))
        {
            var window = (WindowPattern)p; DispatchStarted = true;
            switch (state)
            {
                case "close": window.Close(); break;
                case "minimize": window.SetWindowVisualState(WindowVisualState.Minimized); break;
                case "maximize": window.SetWindowVisualState(WindowVisualState.Maximized); break;
                default: window.SetWindowVisualState(WindowVisualState.Normal); break;
            }
            return new UiaResult([]) { DispatchKind = "window-pattern" };
        }
        throw new GuiException("PatternNotSupported", "WindowPatternUnavailable") { NoEffect = true };
    }
    // ---------------------------------------------------------------- mutation
    /// <summary>Revalidation, state checks, expected value check and dispatch in one worker call.</summary>
    private static UiaResult Mutate(Context ctx)
    {
        var c = ctx.Command; var a = c.Arguments;
        var (element, observed) = Resolve(ctx, c.Target ?? throw new GuiException("InvalidRequest") { NoEffect = true });
        var node = Details(ctx, element, observed);
        var unrealized = node.ElementId.StartsWith("virtual-", StringComparison.Ordinal);
        if (!node.Enabled) throw new GuiException("ElementDisabled") { NoEffect = true };
        var descriptor = Operations.Get(c.Operation);
        foreach (var required in descriptor.RequiredPatterns)
            if (!node.Patterns.Contains(required)) throw new GuiException("PatternNotSupported", "PatternNotSupported: " + required) { NoEffect = true };
        var handle = c.Window.Handle; var id = unrealized ? [] : Ints(node.ElementId);
        bool native = false;
        var modal = Guarded(ctx, () => {
        switch (c.Operation)
        {
            case "set-value":
                if (P(element, ValuePattern.IsReadOnlyProperty) is true) throw new GuiException("ValueReadOnly") { NoEffect = true };
                if (GuiProtocol.OptionalText(a, "expectedValueHash") is { } expected && (!node.Value.Available || GuiProtocol.Hash(node.Value.Text ?? "") != expected)) throw new GuiException("ValueConflict") { NoEffect = true };
                var value = a.GetProperty("value").GetString()!;
                native = !unrealized && NativeAutomation.SetValue(handle, id, value);
                if (!native) { var vp = (ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern); DispatchStarted = true; vp.SetValue(value); }
                break;
            case "set-range-value":
                if (P(element, RangeValuePattern.IsReadOnlyProperty) is true) throw new GuiException("ValueReadOnly") { NoEffect = true };
                var target = a.GetProperty("value").GetDouble();
                if (node.RangeValue is { } range && (target < range.Minimum || target > range.Maximum)) throw new GuiException("InvalidRangeValue", "OutOfRange", new { range.Minimum, range.Maximum }) { NoEffect = true };
                native = !unrealized && NativeAutomation.SetRange(handle, id, target);
                if (!native) { var rp = (RangeValuePattern)element.GetCurrentPattern(RangeValuePattern.Pattern); DispatchStarted = true; rp.SetValue(target); }
                break;
            case "invoke":
                native = !unrealized && NativeAutomation.Invoke(handle, id);
                if (!native) { var ip = (InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern); DispatchStarted = true; ip.Invoke(); }
                break;
            case "select-item":
                native = !unrealized && NativeAutomation.Select(handle, id);
                if (!native) { var sp = (SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern); DispatchStarted = true; sp.Select(); }
                break;
            case "set-toggle":
                var desired = GuiProtocol.Text(a, "desiredState") switch { "on" => 1, "off" => 0, _ => 2 };
                native = !unrealized && NativeAutomation.Toggle(handle, id, desired);
                if (!native)
                {
                    var tp = (TogglePattern)element.GetCurrentPattern(TogglePattern.Pattern); DispatchStarted = true;
                    for (int i = 0; i < 3 && (int)tp.Current.ToggleState != desired; i++) tp.Toggle();
                    if ((int)tp.Current.ToggleState != desired) throw new GuiException("PostconditionFailed", "ToggleStateNotReached");
                }
                break;
            case "set-expanded":
                var expand = a.GetProperty("expanded").GetBoolean();
                native = !unrealized && NativeAutomation.Expand(handle, id, expand);
                if (!native) { var ep = (ExpandCollapsePattern)element.GetCurrentPattern(ExpandCollapsePattern.Pattern); DispatchStarted = true; if (expand) ep.Expand(); else ep.Collapse(); }
                break;
            case "scroll-container":
                double h = a.GetProperty("horizontal").GetDouble(), v = a.GetProperty("vertical").GetDouble();
                native = !unrealized && NativeAutomation.Scroll(handle, id, h, v);
                if (!native) { var scp = (ScrollPattern)element.GetCurrentPattern(ScrollPattern.Pattern); DispatchStarted = true; scp.SetScrollPercent(h, v); }
                break;
            case "set-focus":
                native = !unrealized && NativeAutomation.SetFocus(handle, id);
                if (!native) { DispatchStarted = true; element.SetFocus(); }
                break;
            case "legacy-default-action":
                native = !unrealized && NativeAutomation.DefaultAction(handle, id);
                if (!native) throw new GuiException("StaleElement", "NativeElementNotFound") { NoEffect = true };
                break;
            case "realize-item":
                native = false; var vip = (VirtualizedItemPattern)element.GetCurrentPattern(VirtualizedItemPattern.Pattern); DispatchStarted = true; vip.Realize();
                break;
            case "scroll-item-into-view":
                native = !unrealized && NativeAutomation.ScrollIntoView(handle, id);
                if (!native) { var sip = (ScrollItemPattern)element.GetCurrentPattern(ScrollItemPattern.Pattern); DispatchStarted = true; sip.ScrollIntoView(); }
                break;
            default: throw new GuiException("UnsupportedOperation") { NoEffect = true };
        }
        });
        if (modal != null)
            return new UiaResult([], node) { DispatchKind = "pending-modal", Data = JsonSerializer.SerializeToElement(new { pendingModal = true, modalWindowHandles = modal }) };
        return new UiaResult([], node) { DispatchKind = native ? NativeAutomation.LastDispatchKind : "managed-pattern" };
    }
    /// <summary>Some providers run the action synchronously (WinForms buttons, Win32 dialog buttons): a pattern call that
    /// opens a modal window does not return until that window closes. The call runs on its own thread; when it is still
    /// running after 400 ms and a new window of the target process has appeared, the handles are returned and the call
    /// is left to finish once the modal window is answered. Null means the call finished normally.</summary>
    private static long[]? Guarded(Context ctx, Action act)
    {
        var pid = ctx.Command.Window.Process.Pid; var before = TopWindows(pid);
        Exception? error = null;
        var thread = new Thread(() => { try { act(); } catch (Exception e) { error = e; } }) { IsBackground = true, Name = "uia-dispatch" };
        thread.SetApartmentState(ApartmentState.MTA); thread.Start();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!thread.Join(100))
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds < 400 || !DispatchStarted) continue;
            var fresh = TopWindows(pid).Where(h => !before.Contains(h)).ToArray();
            if (fresh.Length > 0) return fresh;
        }
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
        return null;
    }
    private static HashSet<long> TopWindows(int pid)
    {
        var found = new HashSet<long>();
        Native.EnumWindows((h, _) => { Native.GetWindowThreadProcessId(h, out var p); if (p == pid && Native.IsWindowVisible(h)) found.Add(h.ToInt64()); return true; }, IntPtr.Zero);
        return found;
    }
}

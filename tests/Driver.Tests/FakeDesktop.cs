using System.Text.Json;
using DesktopTestDriver;

/// <summary>An element of the fake UI tree.</summary>
public sealed class FakeElement(string runtimeId, string automationId, string name, string controlType, params string[] patterns)
{
    public string RuntimeId = runtimeId, AutomationId = automationId, Name = name, ControlType = controlType;
    public string[] Patterns = patterns;
    public bool Enabled = true, ReadOnly;
    public string? Value = patterns.Contains("Value") ? "" : null;
    public string? Toggle = patterns.Contains("Toggle") ? "off" : null;
    public PixelRect Bounds = new(10, 10, 80, 20);
    public List<FakeElement> Children = [];
    public FakeElement Add(FakeElement child) { Children.Add(child); return this; }
}
public sealed class FakeInput : IInputBackend
{
    public readonly List<InputEvent> Events = []; public readonly HashSet<(string, int)> Down = [];
    public int SendCalls;
    public bool IsDown(string kind, int code) => Down.Contains((kind, code));
    public void Send(IReadOnlyList<InputEvent> events) { lock (Events) { SendCalls++; Events.AddRange(events); foreach (var e in events) if (e.Kind != "unicode") { if (e.Up) Down.Remove((e.Kind, e.Code)); else Down.Add((e.Kind, e.Code)); } } }
    public void Move(int x, int y) { }
    public void Wheel(int delta) { }
}
/// <summary>Deterministic stand-in for WindowsDesktop and the UIA worker.</summary>
public sealed class FakeDesktop : IGuiDesktop
{
    public static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
    public readonly DesktopProcess Process = Processes.Identify(Environment.ProcessId);
    public readonly Dictionary<long, (string Title, FakeElement Root)> Trees = new();
    public bool Valid { get; set; } = true;
    public bool Foreground = true;
    /// <summary>Milliseconds a mutation blocks inside the "provider" (simulates a hung provider).</summary>
    public int MutationDelay;
    /// <summary>Throw after dispatch began (outcome unknown).</summary>
    public bool FailAfterDispatch;
    public int Invocations, AutomationCalls;
    public FakeDesktop()
    {
        var root = new FakeElement("42-1", "", "메모", "Window", "Window")
            .Add(new FakeElement("42-2", "MemoText", "메모", "Edit", "Value"))
            .Add(new FakeElement("42-3", "SaveButton", "저장", "Button", "Invoke"))
            .Add(new FakeElement("42-4", "", "중복", "Button", "Invoke"))
            .Add(new FakeElement("42-5", "", "중복", "Button", "Invoke"))
            .Add(new FakeElement("42-6", "Result", "결과", "Edit", "Value") { ReadOnly = true, Value = "처음" })
            .Add(new FakeElement("42-7", "Disabled", "비활성", "Button", "Invoke") { Enabled = false })
            .Add(new FakeElement("42-8", "Option", "옵션", "CheckBox", "Toggle"));
        Trees[1001] = ("간단 메모", root);
    }
    public FakeElement Find(string automationId) => All(Trees[1001].Root).First(e => e.AutomationId == automationId);
    private static IEnumerable<FakeElement> All(FakeElement e) => new[] { e }.Concat(e.Children.SelectMany(All));
    public DesktopWindow[] Windows() => Trees.Keys.Select(Identify).ToArray();
    public DesktopWindow Identify(long handle) => Trees.TryGetValue(handle, out var t)
        ? new DesktopWindow(handle, Process, t.Title, new(100, 100, 800, 600), 96, Foreground) { ClassName = "FakeWindow", Kind = "main" }
        : throw new GuiException("WindowNotFound") { NoEffect = true };
    public void Close(DesktopWindow window) => Trees.Remove(window.Handle);
    public void SetState(DesktopWindow window, string state) { }
    public bool Focus(DesktopWindow window) => true;
    public byte[] Capture(DesktopWindow window, PixelRect? region, double scale, CancellationToken cancellation) => Png;
    private string? clipboard;
    public string? ClipboardText() => clipboard;
    public void SetClipboardText(string text) => clipboard = text;
    private ObservationNode Node(DesktopWindow w, FakeElement e, string? parent) =>
        new(e.RuntimeId, parent, e.AutomationId, e.Name, e.ControlType, "", "Fake", e.Enabled, false,
            e.Value == null ? new(false, null, "unavailable") : new(true, e.Value, null, "value"), e.Patterns,
            UiaAutomation.Token(w, e.RuntimeId, parent, e.AutomationId, e.Name, e.ControlType, "", "Fake")) { ToggleState = e.Toggle, Bounds = e.Bounds };
    public UiaResult Automation(string session, UiaCommand c, CancellationToken cancellation)
    {
        AutomationCalls++;
        if (!Trees.TryGetValue(c.Window.Handle, out var tree)) throw new GuiException("WindowNotFound") { NoEffect = true };
        var parents = new Dictionary<string, FakeElement?>(); var order = new List<FakeElement>();
        void Walk(FakeElement e, FakeElement? p) { parents[e.RuntimeId] = p; order.Add(e); foreach (var child in e.Children) Walk(child, e); }
        Walk(tree.Root, null);
        ObservationNode N(FakeElement e) => Node(c.Window, e, parents[e.RuntimeId]?.RuntimeId);
        FakeElement Resolve(ElementLocator l)
        {
            var e = order.FirstOrDefault(x => x.RuntimeId == l.RuntimeId) ?? throw new GuiException("StaleElement") { NoEffect = true };
            if (N(e).SnapshotToken != l.Token) throw new GuiException("StaleElement", "ElementChanged") { NoEffect = true };
            return e;
        }
        SelectorResult FindOne(ElementSelector s)
        {
            var matches = order.Where(e => (s.AutomationId == null || e.AutomationId == s.AutomationId) && (s.Name == null || e.Name == s.Name) && (s.NameContains == null || e.Name.Contains(s.NameContains))
                && (s.ControlType == null || e.ControlType == s.ControlType) && (s.NameRegex == null || GuiProtocol.SafeRegex(s.NameRegex).IsMatch(e.Name))).ToArray();
            if (s.Index is { } i) return i < matches.Length ? new(N(matches[i]), null, matches.Length, "complete") : new(null, new("ElementNotFound", "IndexOutOfRange"), matches.Length, "complete");
            if (matches.Length == 0) return new(null, new("ElementNotFound", "ElementNotFound"), 0, "complete");
            if (matches.Length > 1) return new(null, new("AmbiguousTarget", "AmbiguousTarget", new { matchCount = matches.Length, candidates = matches.Take(5).Select(N).ToArray(), parentNames = matches.Take(5).Select(m => parents[m.RuntimeId]?.Name ?? "").ToArray() }), matches.Length, "complete");
            return new(N(matches[0]), null, 1, "complete");
        }
        switch (c.Operation)
        {
            case "inspect":
                var nodes = order.Select(N).ToArray();
                if (tree.Root.Name.Length > 100_000) return new UiaResult(Enumerable.Range(0, 200).Select(i => nodes[0] with { ElementId = "big-" + i, Name = tree.Root.Name }).ToArray());
                return new UiaResult(nodes);
            case "find-element":
            case "wait-condition":
                var selectors = c.Selectors ?? [c.Selector!];
                var results = selectors.Select(FindOne).ToArray();
                return new UiaResult([], results[0].Target) { Results = results };
            case "capabilities":
                if (c.Target != null) return new UiaResult([], N(Resolve(c.Target)));
                if (c.Elements != null) return new UiaResult([]) { Results = c.Elements.Select(l => { try { return new SelectorResult(N(Resolve(l)), null, 1); } catch (GuiException e) { return new SelectorResult(null, new(e.Code, e.Message)); } }).ToArray() };
                var found = FindOne(c.Selector!); if (found.Error != null) throw new GuiException(found.Error.Code) { NoEffect = true };
                return new UiaResult([], found.Target) { MatchCount = 1 };
            case "hit-test": return new UiaResult([]) { Data = JsonSerializer.SerializeToElement(new { inside = true }) };
            case "window-state": return new UiaResult([]);
            case "generate-selector":
            {
                var e = Resolve(c.Target!);
                return new UiaResult([], N(e)) { Data = JsonSerializer.SerializeToElement(new { selector = new ElementSelector(AutomationId: e.AutomationId) }, GuiProtocol.Json) };
            }
        }
        var target = Resolve(c.Target!);
        if (!target.Enabled) throw new GuiException("ElementDisabled") { NoEffect = true };
        var descriptor = Operations.Get(c.Operation);
        foreach (var p in descriptor.RequiredPatterns) if (!target.Patterns.Contains(p)) throw new GuiException("PatternNotSupported") { NoEffect = true };
        if (c.Operation == "set-value" && target.ReadOnly) throw new GuiException("ValueReadOnly") { NoEffect = true };
        if (MutationDelay > 0) Task.Delay(MutationDelay, cancellation).GetAwaiter().GetResult();
        if (FailAfterDispatch) throw new GuiException("ProviderError", "ProviderFailedDuringDispatch");
        var before = N(target);
        switch (c.Operation)
        {
            case "set-value": target.Value = c.Arguments.GetProperty("value").GetString(); break;
            case "invoke": Invocations++; break;
            case "set-toggle": target.Toggle = c.Arguments.GetProperty("desiredState").GetString(); break;
        }
        return new UiaResult([], before) { DispatchKind = "fake" };
    }
}

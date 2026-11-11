using System.Runtime.InteropServices;

namespace DesktopTestDriver;

// Native UIA3 pattern calls. The element is located by its runtime id, so the provider that was observed
// is the one that receives the call. No focus or input API is used here.
internal static class NativeAutomation
{
    private const int TreeScopeSubtree = 7, RuntimeIdProperty = 30000;
    private static IUIAutomation? client;
    private static readonly Dictionary<string, IUIAutomationElement> bound = [];
    private static IUIAutomation Client() => client ??= (IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new("E22AD333-B25F-460C-83D0-0581107395C9"))!)!;
    internal static string? LastDispatchKind { get; private set; }
    internal static void DisposeWorker()
    {
        foreach (var element in bound.Values) Marshal.ReleaseComObject(element); bound.Clear();
        if (client != null) { Marshal.ReleaseComObject(client); client = null; }
    }
    private static string Key(long handle, int[] id) => handle + "|" + string.Join("-", id);
    /// <summary>Finds the native element with this runtime id under the window. Null when the native and managed trees disagree.</summary>
    internal static IUIAutomationElement? Find(long handle, int[] runtimeId)
    {
        var key = Key(handle, runtimeId);
        if (bound.TryGetValue(key, out var known))
        {
            try { if (known.GetRuntimeId().SequenceEqual(runtimeId)) { LastDispatchKind = "cached-element"; return known; } } catch (COMException) { }
            bound.Remove(key); Marshal.ReleaseComObject(known);
        }
        var c = Client(); IUIAutomationElement? root = null; object? condition = null;
        try
        {
            root = c.ElementFromHandle(new IntPtr(handle));
            if (root.GetRuntimeId().SequenceEqual(runtimeId)) { var keep = root; root = null; Remember(key, keep); LastDispatchKind = "native-window"; return keep; }
            condition = c.CreatePropertyCondition(RuntimeIdProperty, runtimeId);
            var cache = c.CreateCacheRequest();
            try
            {
                cache.put_TreeFilter(c.RawViewCondition());
                var found = root.FindFirstBuildCache(TreeScopeSubtree, condition, cache);
                if (found == null) return null;
                Remember(key, found); LastDispatchKind = "runtime-id-search"; return found;
            }
            finally { Marshal.ReleaseComObject(cache); }
        }
        catch (COMException) { return null; }
        finally { if (condition != null) Marshal.ReleaseComObject(condition); if (root != null) Marshal.ReleaseComObject(root); }
    }
    private static void Remember(string key, IUIAutomationElement element)
    {
        if (bound.Count >= 2048) { foreach (var old in bound.Take(512).ToArray()) { bound.Remove(old.Key); Marshal.ReleaseComObject(old.Value); } }
        bound[key] = element;
    }
    private static T Pattern<T>(IUIAutomationElement element, int patternId) where T : class =>
        element.GetCurrentPattern(patternId) as T ?? throw new GuiException("PatternNotSupported") { NoEffect = true };
    /// <summary>Runs <paramref name="action"/> on the pattern. DispatchStarted is raised immediately before the provider call.</summary>
    private static bool With<T>(long handle, int[] runtimeId, int patternId, Action<T> action) where T : class
    {
        var element = Find(handle, runtimeId); if (element == null) return false;
        var pattern = Pattern<T>(element, patternId);
        try { UiaAutomation.DispatchStarted = true; action(pattern); return true; }
        finally { Marshal.ReleaseComObject(pattern); }
    }
    public static bool SetValue(long handle, int[] id, string value) => With<IUIAutomationValuePattern>(handle, id, 10002, p => p.SetValue(value));
    public static bool Invoke(long handle, int[] id) => With<IUIAutomationInvokePattern>(handle, id, 10000, p => p.Invoke());
    public static bool Select(long handle, int[] id) => With<IUIAutomationSelectionItemPattern>(handle, id, 10010, p => p.Select());
    public static bool Expand(long handle, int[] id, bool expanded) => With<IUIAutomationExpandCollapsePattern>(handle, id, 10005, p => { if (expanded) p.Expand(); else p.Collapse(); });
    public static bool Scroll(long handle, int[] id, double horizontal, double vertical) => With<IUIAutomationScrollPattern>(handle, id, 10004, p => p.SetScrollPercent(horizontal, vertical));
    public static bool SetRange(long handle, int[] id, double value) => With<IUIAutomationRangeValuePattern>(handle, id, 10003, p => p.SetValue(value));
    public static bool DefaultAction(long handle, int[] id) => With<IUIAutomationLegacyIAccessiblePattern>(handle, id, 10018, p => p.DoDefaultAction());
    public static bool ScrollIntoView(long handle, int[] id) => With<IUIAutomationScrollItemPattern>(handle, id, 10017, p => p.ScrollIntoView());
    public static bool Realize(long handle, int[] id) => With<IUIAutomationVirtualizedItemPattern>(handle, id, 10020, p => p.Realize());
    public static bool SetFocus(long handle, int[] id)
    {
        var element = Find(handle, id); if (element == null) return false;
        UiaAutomation.DispatchStarted = true; element.SetFocus(); return true;
    }
    /// <summary>Toggles until the desired state is reached. A three-state control needs up to three calls.</summary>
    public static bool Toggle(long handle, int[] id, int desired) => With<IUIAutomationTogglePattern>(handle, id, 10015, toggle =>
    {
        for (int i = 0; i < 3 && toggle.get_CurrentToggleState() != desired; i++) toggle.Toggle();
        if (toggle.get_CurrentToggleState() != desired) throw new GuiException("PostconditionFailed", "ToggleStateNotReached");
    });
    public static int? ToggleState(long handle, int[] id)
    {
        var element = Find(handle, id); if (element == null) return null;
        var pattern = element.GetCurrentPattern(10015) as IUIAutomationTogglePattern; if (pattern == null) return null;
        try { return pattern.get_CurrentToggleState(); } finally { Marshal.ReleaseComObject(pattern); }
    }
    /// <summary>LegacyIAccessible availability and value (Win32 common controls behind MSAA proxies).</summary>
    public static (bool Available, string? Value) Legacy(long handle, int[] id)
    {
        var element = Find(handle, id); if (element == null) return (false, null);
        try
        {
            if (element.GetCurrentPropertyValue(30090) is not true) return (false, null);
            return (true, element.GetCurrentPropertyValue(30093) as string);
        }
        catch (COMException) { return (false, null); }
    }
    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomation
    {
        void CompareElements(); void CompareRuntimeIds(); void GetRootElement();
        IUIAutomationElement ElementFromHandle(IntPtr handle);
        void ElementFromPoint(); void GetFocusedElement(); void GetRootElementBuildCache(); void ElementFromHandleBuildCache();
        void ElementFromPointBuildCache(); void GetFocusedElementBuildCache(); void CreateTreeWalker();
        void ControlViewWalker(); void ContentViewWalker(); void RawViewWalker();
        [return: MarshalAs(UnmanagedType.Interface)] object RawViewCondition();
        [return: MarshalAs(UnmanagedType.Interface)] object ControlViewCondition();
        [return: MarshalAs(UnmanagedType.Interface)] object ContentViewCondition();
        IUIAutomationCacheRequest CreateCacheRequest(); void CreateTrueCondition(); void CreateFalseCondition();
        [return: MarshalAs(UnmanagedType.Interface)] object CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value);
    }
    [ComImport, Guid("b32a92b5-bc25-4078-9c08-d7ee95c48e03"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationCacheRequest
    {
        void AddProperty(int propertyId); void AddPattern(int patternId); void Clone();
        void get_TreeScope(); void put_TreeScope(int scope); void get_TreeFilter();
        void put_TreeFilter([MarshalAs(UnmanagedType.Interface)] object filter);
    }
    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationElement
    {
        void SetFocus();
        [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)] int[] GetRuntimeId();
        void FindFirst(); void FindAll();
        IUIAutomationElement? FindFirstBuildCache(int scope, [MarshalAs(UnmanagedType.Interface)] object condition, IUIAutomationCacheRequest cache);
        void FindAllBuildCache(); void BuildUpdatedCache();
        [return: MarshalAs(UnmanagedType.Struct)] object GetCurrentPropertyValue(int propertyId);
        void GetCurrentPropertyValueEx(); void GetCachedPropertyValue(); void GetCachedPropertyValueEx();
        void GetCurrentPatternAs(); void GetCachedPatternAs();
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetCurrentPattern(int patternId);
    }
    [ComImport, Guid("a94cd8b1-0844-4cd6-9d2d-640537ab39e9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationValuePattern { void SetValue([MarshalAs(UnmanagedType.BStr)] string value); }
    [ComImport, Guid("fb377fbe-8ea6-46d5-9c73-6499642d3059"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationInvokePattern { void Invoke(); }
    [ComImport, Guid("a8efa66a-0fda-421a-9194-38021f3578ea"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationSelectionItemPattern { void Select(); }
    [ComImport, Guid("619be086-1f4e-4ee4-bafa-210128738730"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationExpandCollapsePattern { void Expand(); void Collapse(); }
    [ComImport, Guid("94cf8058-9b8d-4ab9-8bfd-4cd0a33c8c70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTogglePattern { void Toggle(); int get_CurrentToggleState(); }
    [ComImport, Guid("88f4d42a-e881-459d-a77c-73bbbb7e02dc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationScrollPattern { void Scroll(); void SetScrollPercent(double horizontal, double vertical); }
    [ComImport, Guid("59213f4f-7346-49e5-b120-80555987a148"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationRangeValuePattern { void SetValue(double value); }
    [ComImport, Guid("828055ad-355b-4435-86d5-3b51c14a9b1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationLegacyIAccessiblePattern { void Select(int flags); void DoDefaultAction(); }
    [ComImport, Guid("b488300f-d015-4f19-9c29-bb595e3645ef"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationScrollItemPattern { void ScrollIntoView(); }
    [ComImport, Guid("6ba3d7a6-04cf-4f11-8793-a8d1cde9969f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationVirtualizedItemPattern { void Realize(); }
}

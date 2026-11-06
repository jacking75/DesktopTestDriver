using System.Runtime.InteropServices;

namespace DesktopTestDriver;

// Native UIA3 patterns share the same provider/runtime identity as the observation.
// No focus or input API is called here.
internal static class NativeAutomation
{
    public static void SetValue(long handle, int[] runtimeId, string value)
        => WithPattern(handle,runtimeId,10002,pattern=>((IUIAutomationValuePattern)pattern).SetValue(value));
    public static void Invoke(long handle,int[] runtimeId)
        => WithPattern(handle,runtimeId,10000,pattern=>((IUIAutomationInvokePattern)pattern).Invoke());
    public static void Select(long handle,int[] runtimeId)
        => WithPattern(handle,runtimeId,10010,pattern=>((IUIAutomationSelectionItemPattern)pattern).Select());
    public static void Expand(long handle,int[] runtimeId,bool expanded)
        => WithPattern(handle,runtimeId,10005,pattern=>{if(expanded)((IUIAutomationExpandCollapsePattern)pattern).Expand();else ((IUIAutomationExpandCollapsePattern)pattern).Collapse();});
    public static void Toggle(long handle,int[] runtimeId,bool enabled)
        => WithPattern(handle,runtimeId,10015,pattern=>
        {
            var toggle=(IUIAutomationTogglePattern)pattern;var state=toggle.Current();
            if(state==2)throw new GuiException("TriStateNotSupported");
            if(state!=(enabled?1:0))toggle.Toggle();
            if(toggle.Current()!=(enabled?1:0))throw new GuiException("PostconditionFailed");
        });
    public static void Scroll(long handle,int[] runtimeId,double horizontal,double vertical)
        => WithPattern(handle,runtimeId,10004,pattern=>((IUIAutomationScrollPattern)pattern).SetScrollPercent(horizontal,vertical));
    private static void WithPattern(long handle,int[] runtimeId,int patternId,Action<object> action)
    {
        var client = (IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new("E22AD333-B25F-460C-83D0-0581107395C9"))!)!;
        IUIAutomationElement? root = null; object? condition = null; IUIAutomationElementArray? elements = null;
        try
        {
            root = client.ElementFromHandle(new IntPtr(handle)); condition = client.RawViewCondition();
            elements = root.FindAll(7, condition);
            if (elements.Length() > 2048) throw new GuiException("SearchIncomplete");
            for (int i = 0; i < elements.Length(); i++)
            {
                var element = elements.GetElement(i);
                try
                {
                    if (!element.GetRuntimeId().SequenceEqual(runtimeId)) continue;
                    var pattern = element.GetCurrentPattern(patternId);
                    try { action(pattern); return; }
                    finally { Marshal.ReleaseComObject(pattern); }
                }
                finally { Marshal.ReleaseComObject(element); }
            }
            throw new GuiException("StaleElement");
        }
        finally
        {
            if (elements != null) Marshal.ReleaseComObject(elements);
            if (condition != null) Marshal.ReleaseComObject(condition);
            if (root != null) Marshal.ReleaseComObject(root);
            Marshal.ReleaseComObject(client);
        }
    }
    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements(); void CompareRuntimeIds(); void GetRootElement();
        IUIAutomationElement ElementFromHandle(IntPtr handle);
        void ElementFromPoint(); void GetFocusedElement(); void GetRootElementBuildCache(); void ElementFromHandleBuildCache();
        void ElementFromPointBuildCache(); void GetFocusedElementBuildCache(); void CreateTreeWalker();
        void ControlViewWalker(); void ContentViewWalker(); void RawViewWalker();
        [return: MarshalAs(UnmanagedType.Interface)] object RawViewCondition();
    }
    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();
        [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType=VarEnum.VT_I4)] int[] GetRuntimeId();
        void FindFirst();
        IUIAutomationElementArray FindAll(int scope, [MarshalAs(UnmanagedType.Interface)] object condition);
        void FindFirstBuildCache(); void FindAllBuildCache(); void BuildUpdatedCache();
        void GetCurrentPropertyValue(); void GetCurrentPropertyValueEx(); void GetCachedPropertyValue(); void GetCachedPropertyValueEx();
        void GetCurrentPatternAs(); void GetCachedPatternAs();
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetCurrentPattern(int patternId);
    }
    [ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElementArray
    {
        int Length(); IUIAutomationElement GetElement(int index);
    }
    [ComImport, Guid("a94cd8b1-0844-4cd6-9d2d-640537ab39e9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationValuePattern
    {
        void SetValue([MarshalAs(UnmanagedType.BStr)] string value);
    }
    [ComImport, Guid("fb377fbe-8ea6-46d5-9c73-6499642d3059"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationInvokePattern { void Invoke(); }
    [ComImport, Guid("a8efa66a-0fda-421a-9194-38021f3578ea"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationSelectionItemPattern { void Select(); }
    [ComImport, Guid("619be086-1f4e-4ee4-bafa-210128738730"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationExpandCollapsePattern { void Expand();void Collapse(); }
    [ComImport, Guid("94cf8058-9b8d-4ab9-8bfd-4cd0a33c8c70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTogglePattern { void Toggle();int Current(); }
    [ComImport, Guid("88f4d42a-e881-459d-a77c-73bbbb7e02dc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationScrollPattern { void Scroll();void SetScrollPercent(double horizontal,double vertical); }
}

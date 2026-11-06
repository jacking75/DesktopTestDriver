using System.Runtime.InteropServices;

namespace DesktopTestDriver;

// Native UIA3 patterns share the same provider/runtime identity as the observation.
// No focus or input API is called here.
internal static class NativeAutomation
{
    private static readonly Dictionary<string,IUIAutomationElement> boundElements=[];
    private static string Key(long handle,int[] id)=>handle+"|"+string.Join("-",id);
    internal static string? LastDispatchKind {get;private set;}
    internal static void DisposeWorker(){foreach(var element in boundElements.Values)Marshal.ReleaseComObject(element);boundElements.Clear();}
    private static void Remember(long handle,IUIAutomationElement element,int[] runtimeId)
    {
        var key=Key(handle,runtimeId);if(boundElements.Remove(key,out var prior))Marshal.ReleaseComObject(prior);
        if(boundElements.Count>=1024){var first=boundElements.First();boundElements.Remove(first.Key);Marshal.ReleaseComObject(first.Value);}
        boundElements[key]=element;
    }
    internal static void Bind(long handle,string? scopeRuntimeId,ObservationNode node,int[] runtimeId)
    {
        if(boundElements.ContainsKey(Key(handle,runtimeId)))return;
        var property=node.AutomationId.Length>0?30011:node.ClassName.Length>0?30012:30005;var value=property==30011?node.AutomationId:property==30012?node.ClassName:node.Name;if(value.Length==0)return;
        var client=(IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new("E22AD333-B25F-460C-83D0-0581107395C9"))!)!;IUIAutomationElement? ownedRoot=null;object? condition=null;IUIAutomationElementArray? found=null;
        try
        {
            var root=scopeRuntimeId!=null&&boundElements.TryGetValue(handle+"|"+scopeRuntimeId,out var scope)?scope:ownedRoot=client.ElementFromHandle(new IntPtr(handle));
            condition=client.CreatePropertyCondition(property,value);found=root.FindAll(7,condition);if(found.Length()>2048)throw new GuiException("SearchIncomplete");
            for(int i=0;i<found.Length();i++){var element=found.GetElement(i);var keep=false;try{if(element.GetRuntimeId().SequenceEqual(runtimeId)){Remember(handle,element,runtimeId);keep=true;return;}}finally{if(!keep)Marshal.ReleaseComObject(element);}}
        }
        finally{if(found!=null)Marshal.ReleaseComObject(found);if(condition!=null)Marshal.ReleaseComObject(condition);if(ownedRoot!=null)Marshal.ReleaseComObject(ownedRoot);Marshal.ReleaseComObject(client);}
    }
    internal sealed record NativeQueryMatch(long Handle,int[] RuntimeId);
    internal static NativeQueryMatch[]? FindById(long handle,string automationId,string treeView,bool requireComplete=true)
    {
        var client=(IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new("E22AD333-B25F-460C-83D0-0581107395C9"))!)!;
        var owned=new List<object>();T Own<T>(T value)where T:class{owned.Add(value);return value;}
        try
        {
            var root=Own(client.ElementFromHandle(new IntPtr(handle)));
            var id=Own(client.CreatePropertyCondition(30011,automationId));
            var virtualItem=Own(client.CreatePropertyCondition(System.Windows.Automation.AutomationElement.IsVirtualizedItemPatternAvailableProperty.Id,true));
            var container=Own(client.CreatePropertyCondition(System.Windows.Automation.AutomationElement.IsItemContainerPatternAvailableProperty.Id,true));
            var virtualized=Own(client.CreateOrCondition(virtualItem,container));var query=requireComplete?Own(client.CreateOrCondition(id,virtualized)):id;
            var view=Own(treeView switch{"raw"=>client.RawViewCondition(),"content"=>client.ContentViewCondition(),_=>client.ControlViewCondition()});
            var condition=Own(client.CreateAndCondition(view,query));var elements=Own(root.FindAll(7,condition));if(elements.Length()>1024)throw new GuiException("SearchIncomplete");
            var result=new List<NativeQueryMatch>();bool missingHandle=false;
            for(int i=0;i<elements.Length();i++)
            {
                var element=elements.GetElement(i);
                var keep=false;
                try
                {
                    if(requireComplete&&(element.GetCurrentPropertyValue(System.Windows.Automation.AutomationElement.IsVirtualizedItemPatternAvailableProperty.Id) is true || element.GetCurrentPropertyValue(System.Windows.Automation.AutomationElement.IsItemContainerPatternAvailableProperty.Id) is true))throw new GuiException("SearchIncomplete");
                    if(element.GetCurrentPropertyValue(30011) as string!=automationId)continue;
                    var runtimeId=element.GetRuntimeId();var native=Convert.ToInt64(element.GetCurrentPropertyValue(30020));if(native==0)missingHandle=true;result.Add(new(native,runtimeId));Remember(handle,element,runtimeId);keep=true;
                }
                finally{if(!keep)Marshal.ReleaseComObject(element);}
            }
            return missingHandle?null:result.ToArray();
        }
        finally{for(int i=owned.Count-1;i>=0;i--)Marshal.ReleaseComObject(owned[i]);Marshal.ReleaseComObject(client);}
    }
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
        LastDispatchKind=null;
        if(boundElements.TryGetValue(Key(handle,runtimeId),out var known))
        {
            if(!known.GetRuntimeId().SequenceEqual(runtimeId))throw new GuiException("StaleElement");
            var pattern=known.GetCurrentPattern(patternId);try{LastDispatchKind="cached-element";action(pattern);return;}finally{Marshal.ReleaseComObject(pattern);}
        }
        var client = (IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new("E22AD333-B25F-460C-83D0-0581107395C9"))!)!;
        IUIAutomationElement? root = null; object? condition = null; IUIAutomationElementArray? elements = null;
        try
        {
            root = client.ElementFromHandle(new IntPtr(handle));
            if(root.GetRuntimeId().SequenceEqual(runtimeId))
            {
                var direct=root.GetCurrentPattern(patternId);
                try{LastDispatchKind="native-window";action(direct);return;}finally{Marshal.ReleaseComObject(direct);}
            }
            foreach(var controlView in new[]{true,false})
            {
                condition=controlView?client.ControlViewCondition():client.RawViewCondition();elements=root.FindAll(7,condition);
                if (elements.Length() > 2048) throw new GuiException("SearchIncomplete");
                for (int i = 0; i < elements.Length(); i++)
                {
                    var element = elements.GetElement(i);
                    try
                    {
                        if (!element.GetRuntimeId().SequenceEqual(runtimeId)) continue;
                        var pattern = element.GetCurrentPattern(patternId);
                        try {LastDispatchKind=controlView?"control-tree-scan":"raw-tree-scan";action(pattern); return; }
                        finally { Marshal.ReleaseComObject(pattern); }
                    }
                    finally { Marshal.ReleaseComObject(element); }
                }
                Marshal.ReleaseComObject(elements);elements=null;Marshal.ReleaseComObject(condition);condition=null;
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
        [return: MarshalAs(UnmanagedType.Interface)] object ControlViewCondition();
        [return: MarshalAs(UnmanagedType.Interface)] object ContentViewCondition();
        void CreateCacheRequest();void CreateTrueCondition();void CreateFalseCondition();
        [return: MarshalAs(UnmanagedType.Interface)] object CreatePropertyCondition(int propertyId,[MarshalAs(UnmanagedType.Struct)]object value);
        void CreatePropertyConditionEx();
        [return: MarshalAs(UnmanagedType.Interface)] object CreateAndCondition([MarshalAs(UnmanagedType.Interface)]object first,[MarshalAs(UnmanagedType.Interface)]object second);
        void CreateAndConditionFromArray();void CreateAndConditionFromNativeArray();
        [return: MarshalAs(UnmanagedType.Interface)] object CreateOrCondition([MarshalAs(UnmanagedType.Interface)]object first,[MarshalAs(UnmanagedType.Interface)]object second);
    }
    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();
        [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType=VarEnum.VT_I4)] int[] GetRuntimeId();
        void FindFirst();
        IUIAutomationElementArray FindAll(int scope, [MarshalAs(UnmanagedType.Interface)] object condition);
        void FindFirstBuildCache(); void FindAllBuildCache(); void BuildUpdatedCache();
        [return: MarshalAs(UnmanagedType.Struct)]object GetCurrentPropertyValue(int propertyId);
        void GetCurrentPropertyValueEx(); void GetCachedPropertyValue(); void GetCachedPropertyValueEx();
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

using System.Diagnostics;
using System.Text;
using System.Windows.Automation;

namespace DesktopTestDriver;

public interface IAutomationDesktop
{
    UiaResult Automation(UiaCommand command, CancellationToken cancellation);
    void ReleaseAutomation(string session) { }
}

public static class UiaAutomation
{
    private static WindowsDesktop? workerDesktop;
    private static WindowsDesktop Desktop()
    {
        if(workerDesktop==null){using var process=Process.GetCurrentProcess();workerDesktop=new(process.SessionId,Environment.ProcessPath!,Path.GetTempPath());}return workerDesktop;
    }
    public static void DisposeWorker(){workerDesktop?.Dispose();workerDesktop=null;boundTargets.Clear();virtualItems.Clear();preparedAction=null;NativeAutomation.DisposeWorker();}
    private static CacheRequest MetadataCache()
    {
        var cache=new CacheRequest{TreeScope=TreeScope.Element,AutomationElementMode=AutomationElementMode.Full,TreeFilter=Automation.RawViewCondition};
        foreach(var property in new[]{AutomationElement.RuntimeIdProperty,AutomationElement.AutomationIdProperty,AutomationElement.ControlTypeProperty,AutomationElement.ClassNameProperty,AutomationElement.FrameworkIdProperty,AutomationElement.IsEnabledProperty,AutomationElement.IsOffscreenProperty,AutomationElement.IsPasswordProperty})cache.Add(property);return cache;
    }
    private sealed record PreparedAction(UiaCommand Command,Observation Observation,ObservationNode Target,Dictionary<string,AutomationElement> References,long At)
    {
        public ElementSelector? OriginalSelector {get;init;}=Command.Selector;
        public string? OriginalSearchRoot {get;init;}=Command.SearchRootRuntimeId;
    }
    private static PreparedAction? preparedAction;
    private static readonly Dictionary<string,PreparedAction> boundTargets=[];
    private static string BoundKey(UiaCommand command,string id)=>command.ScopeSession+"|"+command.Window.Handle+"|"+command.Window.Generation+"|"+command.TreeView+"|"+id;
    private static void Remember(UiaCommand command,Observation observation,ObservationNode target,Dictionary<string,AutomationElement> references)
    {
        if(target.ElementId.StartsWith("virtual-",StringComparison.Ordinal))return;
        var byId=observation.Nodes.ToDictionary(n=>n.ElementId);var path=new List<ObservationNode>();var node=target;
        while(true){path.Add(node);if(path.Count>17)return;if(node.ParentId==null)break;if(!byId.TryGetValue(node.ParentId,out node!))return;}
        path.Reverse();var nodes=path.ToArray();var refs=nodes.ToDictionary(n=>n.ElementId,n=>references[n.ElementId]);
        if(boundTargets.Count>=1024&&!boundTargets.ContainsKey(BoundKey(command,target.ElementId)))foreach(var key in boundTargets.OrderBy(p=>p.Value.At).Take(256).Select(p=>p.Key).ToArray())boundTargets.Remove(key);
        boundTargets[BoundKey(command,target.ElementId)]=new(command,observation with{Nodes=nodes,Complete=false,TruncationReason="element-path",QueryComplete=true},target,refs,Stopwatch.GetTimestamp());
    }
    private static PreparedAction Revalidate(UiaCommand command,PreparedAction saved,DesktopWindow actual,TreeWalker walker,bool details)
    {
        if(saved.Command.Window.Process!=command.Window.Process)throw new GuiException("StaleElement","CachedProcessChanged");
        var nodes=new List<ObservationNode>();
        var cache=MetadataCache();
        foreach(var prior in saved.Observation.Nodes)
        {
            var element=saved.References[prior.ElementId].GetUpdatedCache(cache);var c=element.Cached;var id=string.Join("-",(int[])element.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty));var parent=prior.ParentId;
            if(id!=prior.ElementId)throw new GuiException("StaleElement","CachedRuntimeChanged");
            if(parent!=null){var liveParent=walker.GetParent(element);if(liveParent==null||string.Join("-",liveParent.GetRuntimeId())!=parent)throw new GuiException("StaleElement","CachedParentChanged");}
            var name=c.IsPassword?"[password]":element.Current.Name;var type=c.ControlType.ProgrammaticName.Replace("ControlType.","",StringComparison.Ordinal);
            if(c.AutomationId!=prior.AutomationId||type!=prior.ControlType||c.ClassName!=prior.ClassName||c.FrameworkId!=prior.FrameworkId)throw new GuiException("StaleElement","CachedStructureChanged");
            nodes.Add(prior with{Name=name,Enabled=c.IsEnabled,Offscreen=c.IsOffscreen,Value=new(false,null,c.IsPassword?"redacted":"not-requested"),Patterns=[],DetailState="metadata-only"});
        }
        var target=nodes.Single(n=>n.ElementId==saved.Target.ElementId);if(details)target=ReadDetails(target,saved.References[target.ElementId]);nodes[^1]=target;
        var observation=saved.Observation with{Nodes=nodes.ToArray(),QueriedAt=DateTimeOffset.UtcNow};
        if(saved.OriginalSelector!=null&&!ElementQuery.Matches(target,saved.OriginalSelector,observation))throw new GuiException("StaleElement","CachedSelectorChanged");
        return new(command,observation,target,saved.References,Stopwatch.GetTimestamp()){OriginalSelector=saved.OriginalSelector,OriginalSearchRoot=saved.OriginalSearchRoot};
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent,IntPtr child);
    private static bool DirectMutation(string operation)=>operation is "invoke" or "set-value" or "select-item" or "set-toggle" or "set-expanded" or "scroll-container";
    private static readonly Dictionary<string, (AutomationElement Element, ObservationNode Node, string ParentToken)> virtualItems = new();
    private static readonly (AutomationPattern Pattern, string Name)[] Patterns = [
        (InvokePattern.Pattern,"Invoke"), (ValuePattern.Pattern,"Value"), (SelectionItemPattern.Pattern,"SelectionItem"),
        (TogglePattern.Pattern,"Toggle"), (ExpandCollapsePattern.Pattern,"ExpandCollapse"), (ScrollPattern.Pattern,"Scroll"),
        (ScrollItemPattern.Pattern,"ScrollItem"), (VirtualizedItemPattern.Pattern,"VirtualizedItem"),
        (ItemContainerPattern.Pattern,"ItemContainer"), (GridPattern.Pattern,"Grid"), (TablePattern.Pattern,"Table"), (RangeValuePattern.Pattern,"RangeValue")
    ];
    private static readonly AutomationProperty[] PatternAvailability = [AutomationElement.IsInvokePatternAvailableProperty,AutomationElement.IsValuePatternAvailableProperty,AutomationElement.IsSelectionItemPatternAvailableProperty,AutomationElement.IsTogglePatternAvailableProperty,AutomationElement.IsExpandCollapsePatternAvailableProperty,AutomationElement.IsScrollPatternAvailableProperty,AutomationElement.IsScrollItemPatternAvailableProperty,AutomationElement.IsVirtualizedItemPatternAvailableProperty,AutomationElement.IsItemContainerPatternAvailableProperty,AutomationElement.IsGridPatternAvailableProperty,AutomationElement.IsTablePatternAvailableProperty,AutomationElement.IsRangeValuePatternAvailableProperty];
    public static UiaResult Execute(UiaCommand command)
    {
        var prepared=preparedAction;preparedAction=null;
        var desktop=Desktop();
        var actual = desktop.Identify(command.Window.Handle);
        if (!desktop.Valid || actual.Handle != command.Window.Handle || actual.Process != command.Window.Process) throw new GuiException("WindowChanged");
        var walker = command.TreeView switch { "raw" => TreeWalker.RawViewWalker, "content" => TreeWalker.ContentViewWalker, "control" => TreeWalker.ControlViewWalker, _ => throw new GuiException("InvalidTreeView") };
        if(command.RuntimeId!=null&&!command.RuntimeId.StartsWith("virtual-",StringComparison.Ordinal)&&(command.Operation=="capabilities"||DirectMutation(command.Operation))&&boundTargets.TryGetValue(BoundKey(command,command.RuntimeId),out var bound))
        {
            PreparedAction refreshed;
            try{refreshed=Revalidate(command,bound,actual,walker,command.Operation=="capabilities");}
            catch(Exception e)when(e is ElementNotAvailableException or System.Runtime.InteropServices.COMException){boundTargets.Remove(BoundKey(command,command.RuntimeId));throw new GuiException("StaleElement");}
            if(refreshed.Target.SnapshotToken!=command.SnapshotToken)throw new GuiException("StaleElement");boundTargets[BoundKey(command,command.RuntimeId)]=refreshed;
            if(command.Operation=="capabilities"){if(command.IntendedOperation!=null&&DirectMutation(command.IntendedOperation))NativeAutomation.Bind(actual.Handle,bound.OriginalSearchRoot,refreshed.Target,refreshed.References[refreshed.Target.ElementId].GetRuntimeId());return new(refreshed.Observation,refreshed.Target){LookupKind="bound-reference"};}
            return Dispatch(command,desktop,actual,refreshed.Observation,refreshed.Target,refreshed.References[command.RuntimeId]) with{LookupKind="bound-reference"};
        }
        AutomationElement root;
        AutomationElement searchRoot;
        if(command.SearchRootRuntimeId!=null)
        {
            if(!boundTargets.TryGetValue(BoundKey(command,command.SearchRootRuntimeId),out var scope))throw new GuiException("StaleElement");
            var refreshed=Revalidate(command,scope,actual,walker,false);if(refreshed.Target.SnapshotToken!=command.SearchRootSnapshotToken)throw new GuiException("StaleElement");searchRoot=refreshed.References[refreshed.Target.ElementId];root=refreshed.References[refreshed.Observation.Root];
        }
        else {root=AutomationElement.FromHandle(new IntPtr(actual.Handle));searchRoot=root;}
        if(command.RootOnly)
        {
            var c=root.Current;var id=string.Join("-",root.GetRuntimeId());string? parent=null,parentToken=null;var name=c.IsPassword?"[password]":c.Name;var type=c.ControlType.ProgrammaticName.Replace("ControlType.","",StringComparison.Ordinal);
            var token=GuiProtocol.Hash(Encoding.UTF8.GetBytes(GuiProtocol.Encode(new{actual.Process,command.Window.Generation,actual.Handle,id,parent,parentToken,c.AutomationId,name,type,c.ClassName,c.FrameworkId})));
            var node=new ObservationNode(id,null,c.AutomationId,name,type,c.ClassName,c.FrameworkId,c.IsEnabled,c.IsOffscreen,new(false,null,c.IsPassword?"redacted":"not-requested"),[],token){DetailState="metadata-only"};
            var rootObservation=new Observation(id,[node],DateTimeOffset.UtcNow,command.TreeView,false,"root-only",[],"unknown");Remember(command,rootObservation,node,new(){[id]=root});return new(rootObservation){LookupKind="root-only"};
        }
        if(prepared!=null && DirectMutation(command.Operation) && command.RuntimeId==prepared.Target.ElementId && command.SnapshotToken==prepared.Target.SnapshotToken && !command.RuntimeId.StartsWith("virtual-",StringComparison.Ordinal)
            && command.ScopeSession==prepared.Command.ScopeSession && command.TreeView==prepared.Command.TreeView && command.Window.Handle==prepared.Command.Window.Handle && command.Window.Generation==prepared.Command.Window.Generation && command.Window.Process==prepared.Command.Window.Process && Stopwatch.GetElapsedTime(prepared.At).TotalMilliseconds<=1500)
        {
            // 부모·루트까지 실시간 신원을 다시 확인한다. 다른 요청/세션이나 오래된 preflight는 재사용하지 않는다.
            var chain=new List<ObservationNode>();var prior=prepared.Target;
            while(true){chain.Add(prior);if(chain.Count>17)throw new GuiException("SearchIncomplete");if(prior.ParentId==null)break;prior=prepared.Observation.Nodes.Single(n=>n.ElementId==prior.ParentId);}
            chain.Reverse();string? parentToken=null;
            foreach(var saved in chain)
            {
                var live=prepared.References[saved.ElementId];var c=live.Current;var id=string.Join("-",live.GetRuntimeId());var parent=saved.ParentId;
                if(id!=saved.ElementId)throw new GuiException("StaleElement");
                if(parent!=null){var actualParent=walker.GetParent(live);if(actualParent==null||string.Join("-",actualParent.GetRuntimeId())!=parent)throw new GuiException("StaleElement");}
                var name=c.IsPassword?"[password]":c.Name;var type=c.ControlType.ProgrammaticName.Replace("ControlType.","",StringComparison.Ordinal);
                var token=GuiProtocol.Hash(Encoding.UTF8.GetBytes(GuiProtocol.Encode(new{actual.Process,command.Window.Generation,actual.Handle,id,parent,parentToken,c.AutomationId,name,type,c.ClassName,c.FrameworkId})));
                if(token!=saved.SnapshotToken)throw new GuiException("StaleElement");parentToken=token;
            }
            return Dispatch(command,desktop,actual,prepared.Observation,prepared.Target,prepared.References[prepared.Target.ElementId]);
        }
        if(command.Selector?.AutomationId is {Length:>0} exactId && (command.Operation is "find-element" or "wait-condition" or "capabilities" || DirectMutation(command.Operation)))
        {
            // ID 검색은 UIA 엔진에서 창 전체를 대상으로 수행한다. 경로 응답을 전체
            // 트리라고 표시하지 않으며, 가상화가 있으면 완전한 검색으로 인정하지 않는다.
            var idCondition=new PropertyCondition(AutomationElement.AutomationIdProperty,exactId);
            var viewCondition=command.TreeView switch{"raw"=>Automation.RawViewCondition,"content"=>Automation.ContentViewCondition,_=>Automation.ControlViewCondition};
            var nativeMatches=command.SearchRootRuntimeId==null?NativeAutomation.FindById(actual.Handle,exactId,command.TreeView,command.RuntimeId==null):null;
            var found=new List<AutomationElement>();
            if(nativeMatches!=null)foreach(var match in nativeMatches){var element=AutomationElement.FromHandle(new IntPtr(match.Handle));if(!element.GetRuntimeId().SequenceEqual(match.RuntimeId))throw new GuiException("StaleElement");found.Add(element);}
            else
            {
                var virtualCondition=new OrCondition(new PropertyCondition(AutomationElement.IsVirtualizedItemPatternAvailableProperty,true),new PropertyCondition(AutomationElement.IsItemContainerPatternAvailableProperty,true));
                if(command.RuntimeId==null&&searchRoot.FindFirst(TreeScope.Subtree,virtualCondition)!=null)throw new GuiException("SearchIncomplete");
                foreach(AutomationElement element in searchRoot.FindAll(TreeScope.Subtree,new AndCondition(viewCondition,idCondition)))found.Add(element);
            }
            if(found.Count>1024)throw new GuiException("SearchIncomplete");
            if(found.Count>0)
            {
                var rootId=string.Join("-",root.GetRuntimeId());var pathNodes=new Dictionary<string,ObservationNode>();var pathRefs=new Dictionary<string,AutomationElement>();var candidates=new List<ObservationNode>();
                foreach(AutomationElement match in found)
                {
                    var path=new List<AutomationElement>();var cursor=match;
                    while(true){path.Add(cursor);if(path.Count>17)throw new GuiException("SearchIncomplete");if(string.Join("-",cursor.GetRuntimeId())==rootId)break;cursor=walker.GetParent(cursor)??throw new GuiException("StaleElement");}
                    path.Reverse();string? parent=null,parentToken=null;
                    foreach(var element in path)
                    {
                        var c=element.Current;var id=string.Join("-",element.GetRuntimeId());var name=c.IsPassword?"[password]":c.Name;var type=c.ControlType.ProgrammaticName.Replace("ControlType.","",StringComparison.Ordinal);
                        var token=GuiProtocol.Hash(Encoding.UTF8.GetBytes(GuiProtocol.Encode(new{actual.Process,command.Window.Generation,actual.Handle,id,parent,parentToken,c.AutomationId,name,type,c.ClassName,c.FrameworkId})));
                        var node=new ObservationNode(id,parent,c.AutomationId,name,type,c.ClassName,c.FrameworkId,c.IsEnabled,c.IsOffscreen,new(false,null,c.IsPassword?"redacted":"not-requested"),[],token){DetailState="metadata-only"};
                        if(pathNodes.TryGetValue(id,out var seen)&&seen.SnapshotToken!=token)throw new GuiException("StaleElement");
                        pathNodes[id]=node;pathRefs[id]=element;parent=id;parentToken=token;
                    }
                    candidates.Add(pathNodes[string.Join("-",match.GetRuntimeId())]);
                }
                var queryObservation=new Observation(rootId,pathNodes.Values.ToArray(),DateTimeOffset.UtcNow,command.TreeView,false,"selector-path",[],"not-virtualized"){QueryComplete=true};
                var matches=candidates.Where(n=>ElementQuery.Matches(n,command.Selector,queryObservation)&&(command.RuntimeId==null||n.ElementId==command.RuntimeId)).Take(2).ToArray();
                if(matches.Length>1)throw new GuiException("AmbiguousTarget");
                if(matches.Length==1)
                {
                    var queryTarget=ReadDetails(matches[0],pathRefs[matches[0].ElementId]);
                    if(command.RuntimeId!=null&&queryTarget.SnapshotToken!=command.SnapshotToken)throw new GuiException("StaleElement");
                    pathNodes[queryTarget.ElementId]=queryTarget;queryObservation=queryObservation with{Nodes=pathNodes.Values.ToArray()};
                    Remember(command,queryObservation,queryTarget,pathRefs);
                    if(command.Operation=="capabilities"&&command.RuntimeId!=null)preparedAction=new(command,queryObservation,queryTarget,pathRefs,Stopwatch.GetTimestamp());
                    if(AutomationOperations.ReadOnly(command.Operation))return new(queryObservation,queryTarget){LookupKind=command.SearchRootRuntimeId==null?"selector-path":"scoped-selector"};
                    return Dispatch(command,desktop,actual,queryObservation,queryTarget,pathRefs[queryTarget.ElementId]);
                }
                if(command.RuntimeId!=null)throw new GuiException("StaleElement");
            }
            if(command.SearchRootRuntimeId!=null)
            {
                if(command.RuntimeId!=null)throw new GuiException("StaleElement");
                if(command.Operation!="wait-condition")throw new GuiException("ElementNotFound");
                var scope=boundTargets[BoundKey(command,command.SearchRootRuntimeId)];
                return new(scope.Observation with{QueriedAt=DateTimeOffset.UtcNow,QueryComplete=true},null);
            }
            // 부재와 부모 조건 불일치는 기존의 제한된 전체 탐색으로 확인한다.
        }
        // 한 요소의 비민감 메타데이터와 패턴 참조를 한 번에 읽는다. Value/Name은 암호 여부를 확인한 뒤 읽는다.
        // 한 번의 UIA 캐시 갱신으로 같은 시점의 트리를 가져온다. 요소마다 자식/형제
        // 탐색을 왕복하면 대화 이력이 누적될수록 요청 기한을 소진한다.
        // 출력 노드/깊이 제한과 별도 작업자 프로세스의 요청 기한은 그대로 적용한다.
        var cache=new CacheRequest {TreeScope=TreeScope.Subtree,AutomationElementMode=AutomationElementMode.Full,TreeFilter=command.TreeView switch {"raw"=>Automation.RawViewCondition,"content"=>Automation.ContentViewCondition,_=>Automation.ControlViewCondition}};
        foreach(var property in new[]{AutomationElement.RuntimeIdProperty,AutomationElement.AutomationIdProperty,AutomationElement.ControlTypeProperty,AutomationElement.ClassNameProperty,AutomationElement.FrameworkIdProperty,AutomationElement.IsEnabledProperty,AutomationElement.IsOffscreenProperty,AutomationElement.IsPasswordProperty})cache.Add(property);
        foreach(var property in PatternAvailability)cache.Add(property);
        cache.Add(AutomationElement.IsTextPatternAvailableProperty);
        var nodes = new List<ObservationNode>(); var refs = new Dictionary<string, AutomationElement>();
        var prefixCount=0;string? scopeParent=null;
        if(command.SearchRootRuntimeId!=null)
        {
            var scope=boundTargets[BoundKey(command,command.SearchRootRuntimeId)];
            foreach(var ancestor in scope.Observation.Nodes.Where(n=>n.ElementId!=scope.Target.ElementId)){nodes.Add(ancestor);refs[ancestor.ElementId]=scope.References[ancestor.ElementId];}
            prefixCount=nodes.Count;scopeParent=scope.Target.ParentId;
        }
        root=searchRoot.GetUpdatedCache(cache);
        var queue = new Queue<(AutomationElement Element, string? Parent, int Depth)>(); queue.Enqueue((root, scopeParent, prefixCount));
        var errors = new List<string>(); string? truncation = null; int valueCharacters=0;
        while (queue.Count > 0 && nodes.Count < 1024)
        {
            var (element, parent, depth) = queue.Dequeue();
            try
            {
                var c = element.Cached; var id = string.Join("-", (int[])element.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty));
                var patterns = Patterns.Where((p,i) => element.GetCachedPropertyValue(PatternAvailability[i]) is true).Select(p => p.Name).ToArray();
                ElementValue value = new(false, null, "unavailable");
                if (c.IsPassword) value = new(false, null, "redacted");
                else if (patterns.Contains("Value") && element.TryGetCurrentPattern(ValuePattern.Pattern, out var v)) value = new(true, ((ValuePattern)v).Current.Value, null);
                else if (element.GetCachedPropertyValue(AutomationElement.IsTextPatternAvailableProperty) is true && element.TryGetCurrentPattern(TextPattern.Pattern, out var t)) value = new(true, ((TextPattern)t).DocumentRange.GetText(65537), null);
                if (value.Text?.Length > 65536) value = new(false, null, "tooLarge");
                if(value.Text!=null) { if(valueCharacters+value.Text.Length>65536) value=new(false,null,"tooLarge"); else valueCharacters+=value.Text.Length; }
                var type = c.ControlType.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal);
                var name = c.IsPassword ? "[password]" : element.Current.Name;
                var token = GuiProtocol.Hash(Encoding.UTF8.GetBytes(GuiProtocol.Encode(new { actual.Process, command.Window.Generation, actual.Handle, id, parent, parentToken = nodes.FirstOrDefault(n => n.ElementId == parent)?.SnapshotToken, c.AutomationId, name, type, c.ClassName, c.FrameworkId })));
                var toggle = patterns.Contains("Toggle") && element.TryGetCurrentPattern(TogglePattern.Pattern, out var tp) ? ((TogglePattern)tp).Current.ToggleState.ToString().ToLowerInvariant() : null;
                nodes.Add(new(id, parent, c.AutomationId, name, type, c.ClassName, c.FrameworkId, c.IsEnabled, c.IsOffscreen, value, patterns, token, toggle)); refs[id] = element;
                var children=element.CachedChildren;
                if (depth >= 16 && children.Count>0) { truncation = "depth-limit"; continue; }
                foreach(AutomationElement child in children)
                {
                    if (queue.Count + nodes.Count >= 2048) { truncation = "node-limit"; break; }
                    queue.Enqueue((child, id, depth + 1));
                }
            }
            catch (Exception e) when (e is ElementNotAvailableException or System.Runtime.InteropServices.COMException or InvalidOperationException)
            { errors.Add("ProviderReadFailed"); }
        }
        if (queue.Count > 0) truncation = "node-limit";
        var observation = new Observation(nodes.FirstOrDefault()?.ElementId ?? "", nodes.ToArray(), DateTimeOffset.UtcNow, command.TreeView,
            truncation == null && errors.Count == 0 && !nodes.Any(n => n.Patterns.Contains("VirtualizedItem") || n.Patterns.Contains("ItemContainer")), truncation ?? (nodes.Any(n => n.Patterns.Contains("VirtualizedItem") || n.Patterns.Contains("ItemContainer")) ? "virtualized-items-not-enumerated" : null), errors.Distinct().ToArray(), nodes.Any(n => n.Patterns.Contains("ItemContainer") || n.Patterns.Contains("VirtualizedItem")) ? "partial-or-virtualized" : "unknown");
        ObservationNode? target = null;
        if(command.SearchRootRuntimeId!=null)
        {
            if(!observation.Complete)throw new GuiException("SearchIncomplete");
            var matching=nodes.Skip(prefixCount).Where(n=>ElementQuery.Matches(n,command.Selector!,observation)).Take(2).ToArray();
            if(matching.Length>1)throw new GuiException("AmbiguousTarget");if(matching.Length==0&&command.Operation!="wait-condition")throw new GuiException("ElementNotFound");target=matching.SingleOrDefault();
            observation=observation with{Root=nodes[0].ElementId,Complete=false,QueryComplete=true,TruncationReason="search-root-subtree"};
        }
        else if (command.Selector != null&&command.RuntimeId==null) target = ElementQuery.Find(observation, command.Selector, command.Operation == "wait-condition");
        if (command.RuntimeId != null && virtualItems.TryGetValue(command.RuntimeId, out var savedItem))
        {
            if (savedItem.Node.SnapshotToken != command.SnapshotToken || !nodes.Any(n => n.SnapshotToken == savedItem.ParentToken)) throw new GuiException("StaleElement");
            target = savedItem.Node; refs[target.ElementId] = savedItem.Element; nodes.Add(target);
            observation = observation with { Nodes = nodes.ToArray() };
        }
        else if (command.RuntimeId != null)
        {
            target = nodes.SingleOrDefault(n => n.ElementId == command.RuntimeId) ?? throw new GuiException("StaleElement");
            if (target.SnapshotToken != command.SnapshotToken) throw new GuiException("StaleElement");
        }
        if (command.Operation == "find-item")
        {
            if (target == null || command.ItemName == null) throw new GuiException("InvalidItemQuery");
            if (!refs[target.ElementId].TryGetCurrentPattern(ItemContainerPattern.Pattern, out var pattern)) throw new GuiException("PatternNotSupported");
            var container = (ItemContainerPattern)pattern;
            var item = container.FindItemByProperty(null, AutomationElement.NameProperty, command.ItemName);
            if (item == null) throw new GuiException("ElementNotFound");
            if (container.FindItemByProperty(item, AutomationElement.NameProperty, command.ItemName) != null) throw new GuiException("AmbiguousTarget");
            var id = "virtual-" + Guid.NewGuid().ToString("N");
            var token = GuiProtocol.Hash(Encoding.UTF8.GetBytes(command.Window.Process.ImageHash + target.SnapshotToken + id));
            var supported = Patterns.Where(p => item.TryGetCurrentPattern(p.Pattern, out _)).Select(p => p.Name).ToArray();
            var found = new ObservationNode(id, target.ElementId, "", command.ItemName, "ListItem", "", "", true, true, new(false, null, "unavailable"), supported, token);
            if (virtualItems.Count >= 1024) throw new GuiException("VirtualItemBudgetExceeded");
            virtualItems[id] = (item, found, target.SnapshotToken);
            return new(observation with { Nodes = observation.Nodes.Append(found).ToArray() }, found);
        }
        if (AutomationOperations.ReadOnly(command.Operation))
        {
            if(target!=null)Remember(command,observation,target,refs);
            else if(command.Operation=="inspect")foreach(var node in observation.Nodes)Remember(command,observation,node,refs);
            if(command.Operation=="capabilities" && target!=null && command.RuntimeId!=null && !command.RuntimeId.StartsWith("virtual-",StringComparison.Ordinal))preparedAction=new(command,observation,target,refs,Stopwatch.GetTimestamp());
            return new(observation, target);
        }
        if (target == null) throw new GuiException("ElementNotFound");
        return Dispatch(command,desktop,actual,observation,target,refs[target.ElementId]);
    }
    private static ObservationNode ReadDetails(ObservationNode node,AutomationElement element)
    {
        var cache=new CacheRequest{TreeScope=TreeScope.Element,AutomationElementMode=AutomationElementMode.Full,TreeFilter=Automation.RawViewCondition};foreach(var property in PatternAvailability)cache.Add(property);cache.Add(AutomationElement.IsPasswordProperty);cache.Add(AutomationElement.IsTextPatternAvailableProperty);
        var cached=element.GetUpdatedCache(cache);var patterns=Patterns.Where((p,index)=>cached.GetCachedPropertyValue(PatternAvailability[index]) is true).Select(p=>p.Name).ToArray();ElementValue value=new(false,null,"unavailable");
        if(cached.Cached.IsPassword)value=new(false,null,"redacted");
        else if(element.TryGetCurrentPattern(ValuePattern.Pattern,out var vp))value=new(true,((ValuePattern)vp).Current.Value,null);
        else if(element.TryGetCurrentPattern(TextPattern.Pattern,out var tp))value=new(true,((TextPattern)tp).DocumentRange.GetText(65537),null);
        if(value.Text?.Length>65536)value=new(false,null,"tooLarge");
        var toggle=element.TryGetCurrentPattern(TogglePattern.Pattern,out var toggled)?((TogglePattern)toggled).Current.ToggleState.ToString().ToLowerInvariant():null;
        return node with{Patterns=patterns,Value=value,ToggleState=toggle,DetailState="complete"};
    }
    private static UiaResult Dispatch(UiaCommand command,WindowsDesktop desktop,DesktopWindow actual,Observation observation,ObservationNode target,AutomationElement node)
    {
        var unrealizedMetadata=target.ElementId.StartsWith("virtual-",StringComparison.Ordinal)&&command.Operation is "realize-item" or "scroll-item-into-view";
        if (!target.Enabled || !unrealizedMetadata&&!node.Current.IsEnabled) throw new GuiException("ElementDisabled");
        T Pattern<T>(AutomationPattern pattern) => node.TryGetCurrentPattern(pattern, out var p) ? (T)p : throw new GuiException("PatternNotSupported");
        // Identity is checked again immediately before provider dispatch.
        if (!WindowRegistry.Same(actual, desktop.Identify(actual.Handle))) throw new GuiException("WindowChanged");
        var nativeHandle=unrealizedMetadata?0:node.Current.NativeWindowHandle;
        long patternRoot=actual.Handle;
        if(nativeHandle!=0)
        {
            var handle=new IntPtr(nativeHandle);Native.GetWindowThreadProcessId(handle,out var pid);
            if(pid==actual.Process.Pid && (handle==new IntPtr(actual.Handle)||IsChild(new IntPtr(actual.Handle),handle)))patternRoot=handle.ToInt64();
        }
        switch (command.Operation)
        {
            case "invoke": NativeAutomation.Invoke(patternRoot,node.GetRuntimeId()); break;
            case "set-value":
                if (node.Current.IsPassword) throw new GuiException("PasswordTargetDenied");
                var value = Pattern<ValuePattern>(ValuePattern.Pattern); if (value.Current.IsReadOnly) throw new GuiException("ValueReadOnly"); NativeAutomation.SetValue(patternRoot, node.GetRuntimeId(), command.Value!); break;
            case "select-item": NativeAutomation.Select(patternRoot,node.GetRuntimeId()); break;
            case "set-toggle": NativeAutomation.Toggle(patternRoot,node.GetRuntimeId(),command.DesiredState=="on");break;
            case "set-expanded": NativeAutomation.Expand(patternRoot,node.GetRuntimeId(),command.Expanded==true);break;
            case "realize-item": Pattern<VirtualizedItemPattern>(VirtualizedItemPattern.Pattern).Realize(); break;
            case "scroll-item-into-view": Pattern<ScrollItemPattern>(ScrollItemPattern.Pattern).ScrollIntoView(); break;
            case "scroll-container": NativeAutomation.Scroll(patternRoot,node.GetRuntimeId(),command.Horizontal!.Value,command.Vertical!.Value); break;
            default: throw new GuiException("UnsupportedOperation");
        }
        return new(observation, target, "dispatched"){DispatchKind=command.Operation is "realize-item" or "scroll-item-into-view"?"provider-pattern":NativeAutomation.LastDispatchKind};
    }
}

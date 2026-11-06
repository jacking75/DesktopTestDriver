using DesktopTestDriver;
using System.Diagnostics;
using System.Text.Json;

static class AutomationFlowTests
{
    public static async Task<int> Run()
    {
        var store=new StateStore(Path.GetFullPath(Path.Combine("artifacts","automation-flow",Guid.NewGuid().ToString("N"))));
        using var process=Process.GetCurrentProcess(); var identity=Processes.Identify(process);
        var desktop=new SemanticDesktop(identity); var sessions=new ControlSessions(store,"fixture",1,"dedicated");
        var session=sessions.Open("control",120,2,"isolated-desktop",[identity]);
        var input=new FakeInput(); var handler=new GuiRequestHandler(store,desktop,sessions,new(store,input),machineUsage:"dedicated");
        void Check(bool ok,string message) {if(!ok) throw new Exception(message);}
        async Task<GuiResponse> Request(string op,object args,string? id=null,int timeout=5000)
        {
            var request=GuiProtocol.Parse(GuiProtocol.Encode(new{apiVersion=2,requestId=id??Guid.NewGuid().ToString("N"),controlSessionId=session.ControlSessionId,operation=op,arguments=args,timeoutMilliseconds=timeout}));
            return await handler.Execute(new(request,timeout));
        }
        JsonElement Data(GuiResponse response)=>JsonSerializer.SerializeToElement(response.Data,GuiProtocol.Json);
        var window=Data(await Request("windows",new{})).GetProperty("windows")[0].GetProperty("windowId").GetString()!;
        async Task<JsonElement> Find(string id)=>Data(await Request("find-element",new{windowId=window,selector=new{automationId=id}})).GetProperty("target");
        async Task<GuiResponse> Mutation(string op,JsonElement target,string id)=>await Request(op,new{windowId=window,elementId=target.GetProperty("elementId").GetString(),snapshotToken=target.GetProperty("snapshotToken").GetString(),value="한글"},id);
        var editor=await Find("Editor");
        var first=await Mutation("set-value",editor,"once"); var duplicate=await Mutation("set-value",editor,"once");
        Check(first.Status=="completed" && duplicate.Status=="completed" && desktop.Mutations==1,"duplicate provider dispatch");
        var wait=await Request("wait-condition",new{windowId=window,selector=new{automationId="Editor"},condition="value-equals",expected="한글",pollMilliseconds=100});
        Check(wait.Status=="completed" && input.Events.Count==0,"condition/read-only input");
        desktop.ReadOnlyPattern=true;
        var unsupported=await Mutation("set-value",await Find("Editor"),"unsupported");
        Check(unsupported.Error?.Code=="PatternNotSupported" && desktop.Mutations==1,"unsupported fallback dispatched"); desktop.ReadOnlyPattern=false;
        var stale=await Find("Editor"); desktop.Generation++;
        Check((await Mutation("set-value",stale,"stale")).Error?.Code=="StaleElement" && desktop.Mutations==1,"stale dispatched");
        Console.WriteLine("PASS S08/S09 semantic dispatch, duplicate once, wait, stale and unsupported without input fallback (synthetic provider)");
        var timeoutResult=await Request("wait-condition",new{windowId=window,selector=new{automationId="Missing"},condition="exists",pollMilliseconds=100},timeout:100);
        Check(timeoutResult.Error?.Code=="RequestTimeout" && desktop.Mutations==1,"wait deadline ignored");
        desktop.ThrowAfterDispatch=true;
        var unknown=await Mutation("set-value",await Find("Editor"),"unknown");
        Check(unknown.Status=="completionUnknown" && sessions.Get(session.ControlSessionId).Info.State=="blocked","unknown did not pause");
        Check((await Mutation("set-value",editor,"next")).Error?.Code=="SessionBlocked","unknown allowed next mutation");
        var state=sessions.Get(session.ControlSessionId).Info;
        var reconciled=sessions.Reconcile(session.ControlSessionId,"unknown","abandoned","fixture-evidence",state.Revision);
        sessions.Resume(session.ControlSessionId,reconciled.Revision);
        Check(handler.Result(session.ControlSessionId,"unknown").Status=="completionUnknown" && desktop.Mutations==2,"reconciliation rewrote/replayed original");
        Console.WriteLine("PASS S09 deadline, unknown pause, explicit reconciliation and original outcome preservation");
        await handler.Release(sessions.Get(session.ControlSessionId),true,"TestEnd");
        return 2;
    }
    private sealed class SemanticDesktop(DesktopProcess identity) : IGuiDesktop,IAutomationDesktop
    {
        public int Mutations,Generation; public bool ReadOnlyPattern,ThrowAfterDispatch; private string text="";
        private DesktopWindow Window=>new(1,identity,"fixture",new(0,0,200,100),96,false);
        public bool Valid=>true; public DesktopWindow[] Windows()=>[Window]; public DesktopWindow Identify(long handle)=>Window;
        public DesktopElement[] Controls(DesktopWindow window,CancellationToken cancellation)=>[];
        public byte[] Capture(DesktopWindow window,CancellationToken cancellation)=>throw new Exception("unexpected capture");
        public bool Focus(DesktopWindow window)=>throw new Exception("unexpected focus"); public void Close(DesktopWindow window)=>throw new Exception("unexpected close");
        public UiaResult Automation(UiaCommand command,CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var node=new ObservationNode("editor",null,"Editor","編集","Edit","fixture","fixture",true,false,new(true,text,null),ReadOnlyPattern?[]:["Value"],"token-"+Generation);
            var observation=new Observation("editor",[node],DateTimeOffset.UtcNow,"control",true,null,[],"none");
            var target=command.RuntimeId==null ? command.Selector==null?null:ElementQuery.Find(observation,command.Selector,command.Operation=="wait-condition") : node;
            if(command.RuntimeId!=null && command.SnapshotToken!=node.SnapshotToken) throw new GuiException("StaleElement");
            if(!AutomationOperations.ReadOnly(command.Operation)) {Mutations++;text=command.Value??"";if(ThrowAfterDispatch) throw new OperationCanceledException();}
            return new(observation,target);
        }
    }
}

using System.Diagnostics;
using System.Text.Json;
using DesktopTestDriver;

static class SemanticProfileProbe
{
    // 프로필 최초 인증용 시험 경로이다. 전역 입력 backend와 InteractionGuard를
    // 연결하지 않고, 지정한 프로세스 신원 안의 UIA 의미 조작만 실행한다.
    public static async Task<int> Run(string configuration)
    {
        var config=JsonDocument.Parse(File.ReadAllText(configuration)).RootElement;
        var output=config.GetProperty("evidencePath").GetString()!;Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var scope=GuiProtocol.Decode<DesktopProcess[]>(File.ReadAllText(config.GetProperty("scopeFile").GetString()!));
        if(scope.Length!=1)throw new Exception("인증 대상은 독립 프로세스 하나여야 한다.");
        using var target=Process.GetProcessById(scope[0].Pid);if(Processes.Identify(target)!=scope[0])throw new Exception("인증 대상 신원이 변경됐다.");
        using var current=Process.GetCurrentProcess();using var desktop=new WindowsDesktop(current.SessionId,Environment.ProcessPath!,Path.GetDirectoryName(output)!);
        var store=new StateStore(Path.Combine(Path.GetDirectoryName(output)!,"semantic-probe-state"));var sessions=new ControlSessions(store,"probe",current.SessionId,"dedicated");var input=new FakeInput();var handler=new GuiRequestHandler(store,desktop,sessions,new GuiInput(store,input),machineUsage:"dedicated");
        var session=sessions.Open("control",120,2,"isolated-desktop",scope);using var foreground=new ForegroundProbe(scope[0].Pid);
        var metrics=new List<object>();string? error=null;bool closed=false;var requests=0;
        async Task<JsonElement> Request(string operation,object arguments)
        {
            if(operation is not("windows" or "inspect" or "find-element" or "wait-condition" or "set-value" or "invoke"))throw new Exception("인증 경로는 UIA 의미 조작만 허용한다.");
            var clock=Stopwatch.StartNew();var request=GuiProtocol.Parse(GuiProtocol.Encode(new{apiVersion=2,requestId="probe-"+(++requests),controlSessionId=session.ControlSessionId,operation,timeoutMilliseconds=5000,arguments}));
            var result=await handler.Execute(new(request,5000));metrics.Add(new{operation,milliseconds=clock.ElapsedMilliseconds,status=result.Status});
            if(result.Status!="completed")throw new Exception(GuiProtocol.Encode(result));return JsonSerializer.SerializeToElement(result.Data,GuiProtocol.Json);
        }
        try
        {
            var matches=new List<string>();var windows=(await Request("windows",new{})).GetProperty("windows");
            foreach(var w in windows.EnumerateArray()){var id=w.GetProperty("windowId").GetString()!;var root=(await Request("inspect",new{windowId=id,rootOnly=true})).GetProperty("observation").GetProperty("nodes")[0];if(root.GetProperty("automationId").GetString()==config.GetProperty("windowAutomationId").GetString())matches.Add(id);}
            if(matches.Count!=1)throw new Exception("인증 대화 창이 유일하지 않다.");var window=matches[0];
            foreach(var step in config.GetProperty("steps").EnumerateArray())
            {
                var selector=step.GetProperty("selector");var found=(await Request("find-element",new{windowId=window,selector})).GetProperty("target");
                var elementId=found.GetProperty("elementId").GetString();var snapshotToken=found.GetProperty("snapshotToken").GetString();
                if(step.GetProperty("operation").GetString()=="set-value")
                {
                    var value=step.GetProperty("value").GetString()!;await Request("set-value",new{windowId=window,elementId,snapshotToken,value});
                    await Request("wait-condition",new{windowId=window,selector,condition="value-equals",expected=value});
                }
                else if(step.GetProperty("operation").GetString()=="invoke")
                {
                    await Request("invoke",new{windowId=window,elementId,snapshotToken});
                    await Request("wait-condition",new{windowId=window,selector=config.GetProperty("completionSelector"),condition="text-contains",expected=config.GetProperty("completionText").GetString()});
                }
                else throw new Exception("지원하지 않는 인증 동작이다.");
            }
            if(input.Events.Count!=0||foreground.Count!=0)throw new Exception("전역 입력 또는 대상 전경 전환을 감지했다.");
        }
        catch(Exception e){error=e.Message;}
        finally{closed=await handler.Release(sessions.Get(session.ControlSessionId),true,"ProbeEnd");}
        var passed=error==null&&closed&&input.Events.Count==0&&foreground.Count==0;
        File.WriteAllText(output,GuiProtocol.Encode(new{passed,error,closed,globalInputEvents=input.Events.Count,targetForegroundEvents=foreground.Count,metrics,scope,method="scoped semantic-only certification; no global input backend",at=DateTimeOffset.UtcNow}));
        Console.WriteLine(passed?"PASS semantic profile probe":"FAIL semantic profile probe");return passed?0:1;
    }
}

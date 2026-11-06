using DesktopTestDriver;
using System.Diagnostics;
using System.Text.Json;

static class WindowUiTests
{
    public static async Task<int> Run()
    {
        var output=Path.GetFullPath(Path.Combine("artifacts","window-ui-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(output);
        var exe=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","FormsWorkbench","FormsWorkbench.exe"));
        var start=new ProcessStartInfo(exe){UseShellExecute=false};start.ArgumentList.Add("--quiet");
        using var process=Process.Start(start)!;using var job=new ApplicationJob("window-test-"+Guid.NewGuid().ToString("N"),process);var identity=Processes.Identify(process);
        File.WriteAllText(Path.Combine(output,"owned.json"),GuiProtocol.Encode(identity));
        using var current=Process.GetCurrentProcess();using var desktop=new WindowsDesktop(current.SessionId,Environment.ProcessPath!,output);
        var store=new StateStore(Path.Combine(output,"state"));var sessions=new ControlSessions(store,"fixture",current.SessionId,"dedicated");
        var profiles=new ProfileRegistry(store);var profile=new ApplicationProfile(1,"fixture","windows-uia",new("exe","work","hash"),new(){["editor"]=new(AutomationId:"Editor")},new(){["set"]=new("editor","set-value")},CaptureAllowed:true);
        profiles.Register(GuiProtocol.Encode(profile),new(new(){["exe"]=exe,["work"]=Path.GetDirectoryName(exe)!},new(){["hash"]=identity.ImageHash}));
        var session=sessions.Open("control",120,2,"isolated-desktop",[identity],"fixture");var input=new FakeInput();var handler=new GuiRequestHandler(store,desktop,sessions,new(store,input),machineUsage:"dedicated");
        string verdict="FAIL";string? error=null;bool closed=false,ended=false,occlusionConfirmed=false;var stages=new List<string>();
        async Task<GuiResponse> Raw(string operation,object arguments)
        {
            var request=GuiProtocol.Parse(GuiProtocol.Encode(new{apiVersion=2,requestId=Guid.NewGuid().ToString("N"),controlSessionId=session.ControlSessionId,operation,timeoutMilliseconds=5000,arguments}));return await handler.Execute(new(request,5000));
        }
        async Task<JsonElement> Request(string operation,object arguments){var result=await Raw(operation,arguments);if(result.Status!="completed")throw new Exception(GuiProtocol.Encode(result));return JsonSerializer.SerializeToElement(result.Data,GuiProtocol.Json);}
        try
        {
            var main=(await Request("wait-window",new{title="독립 GUI 검증 도구"})).GetProperty("window").GetProperty("windowId").GetString()!;
            async Task<JsonElement> Find(string id)=>(await Request("find-element",new{windowId=main,selector=new{automationId=id}})).GetProperty("target");
            async Task Invoke(string id){var target=await Find(id);await Request("invoke",new{windowId=main,elementId=target.GetProperty("elementId").GetString(),snapshotToken=target.GetProperty("snapshotToken").GetString()});}
            await Invoke("OpenChild");
            var child=(await Request("wait-window",new{title="보조 창",ownerWindowId=main})).GetProperty("window").GetProperty("windowId").GetString()!;
            var capture=await Raw("capture",new{windowId=main,output=Path.Combine(output,"occluded.png")});
            occlusionConfirmed=capture.Error?.Code=="ObservationUnavailable" && capture.Error.Message.Contains("ClientOccluded",StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(output,"occlusion.json"),GuiProtocol.Encode(capture));
            var editor=await Find("Editor");await Request("set-value",new{windowId=main,elementId=editor.GetProperty("elementId").GetString(),snapshotToken=editor.GetProperty("snapshotToken").GetString(),value="가려진 창의 한글"});
            await Request("wait-condition",new{windowId=main,selector=new{automationId="Editor"},condition="value-equals",expected="가려진 창의 한글"});
            await Request("close-window",new{windowId=child});stages.Add("owned-child-and-occluded-uia");
            await Invoke("OpenModal");var modal=(await Request("wait-window",new{title="모달 창",ownerWindowId=main})).GetProperty("window").GetProperty("windowId").GetString()!;
            await Request("inspect",new{windowId=modal});await Request("close-window",new{windowId=modal});stages.Add("owned-modal");
            await Request("wait-condition",new{windowId=main,selector=new{automationId="Save"},condition="enabled"});
            await Invoke("Save");var fileDialog=(await Request("wait-window",new{className="#32770",ownerWindowId=main})).GetProperty("window").GetProperty("windowId").GetString()!;
            await Request("close-window",new{windowId=fileDialog});stages.Add("owned-file-dialog");
            await Request("wait-condition",new{windowId=main,selector=new{automationId="Replace"},condition="enabled"});
            var before=await Find("Editor");await Invoke("Replace");
            await Request("wait-condition",new{windowId=main,selector=new{automationId="Editor",name="새 편집"},condition="exists"});
            var stale=await Raw("set-value",new{windowId=main,elementId=before.GetProperty("elementId").GetString(),snapshotToken=before.GetProperty("snapshotToken").GetString(),value="실행되면 안 된다"});
            if(stale.Error?.Code!="StaleElement")throw new Exception("교체된 요소의 실행을 거부하지 않았다.");stages.Add("dynamic-stale-rejected");
            if(!occlusionConfirmed||input.Events.Count!=0)throw new Exception("가림 또는 전역 입력 0을 확인하지 못했다.");
            verdict="PASS";
        }
        catch(Exception e){error=e.Message;}
        finally{closed=await handler.Release(sessions.Get(session.ControlSessionId),true,"TestEnd");ended=Processes.KillOwned(identity);if(!closed||!ended)verdict="FAIL";File.WriteAllText(Path.Combine(output,"summary.json"),GuiProtocol.Encode(new{verdict,error,stages,occlusionConfirmed,inputEvents=input.Events.Count,closed,ended,policy="isolated-desktop",modalBackgroundQualification=false}));}
        Console.WriteLine(verdict+" 실제 owned 창·모달·파일 대화상자·가려진 UIA·동적 교체·정리이다. "+output);return verdict=="PASS"?0:1;
    }
}

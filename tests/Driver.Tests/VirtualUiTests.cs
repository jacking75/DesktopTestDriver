using DesktopTestDriver;
using System.Diagnostics;
using System.Text.Json;

static class VirtualUiTests
{
    public static async Task<int> Run()
    {
        var output=Path.GetFullPath(Path.Combine("artifacts","virtual-ui-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(output);
        var start=new ProcessStartInfo(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","SimpleMemo","SimpleMemo.exe"))){UseShellExecute=false};start.ArgumentList.Add("--quiet");start.ArgumentList.Add("--virtualized");
        using var process=Process.Start(start)!;using var job=new ApplicationJob("virtual-test-"+Guid.NewGuid().ToString("N"),process);var identity=Processes.Identify(process);
        File.WriteAllText(Path.Combine(output,"owned.json"),GuiProtocol.Encode(identity));
        using var current=Process.GetCurrentProcess();using var desktop=new WindowsDesktop(current.SessionId,Environment.ProcessPath!,output);
        var store=new StateStore(Path.Combine(output,"state"));var sessions=new ControlSessions(store,"fixture",current.SessionId,"dedicated");
        var session=sessions.Open("control",120,2,"isolated-desktop",[identity]);var fakeInput=new FakeInput();var handler=new GuiRequestHandler(store,desktop,sessions,new(store,fakeInput),machineUsage:"dedicated");
        string verdict="FAIL";string? error=null;bool closed=false,ended=false;
        async Task<JsonElement> Request(string operation,object arguments)
        {
            var request=GuiProtocol.Parse(GuiProtocol.Encode(new{apiVersion=2,requestId=Guid.NewGuid().ToString("N"),controlSessionId=session.ControlSessionId,operation,timeoutMilliseconds=5000,arguments}));
            var response=await handler.Execute(new(request,5000));if(response.Status!="completed")throw new Exception(GuiProtocol.Encode(response));return JsonSerializer.SerializeToElement(response.Data,GuiProtocol.Json);
        }
        try
        {
            var began=Stopwatch.GetTimestamp();string? window=null;
            while(window==null && Stopwatch.GetElapsedTime(began).TotalSeconds<5){var found=(await Request("windows",new{})).GetProperty("windows");if(found.GetArrayLength()>0)window=found[0].GetProperty("windowId").GetString();else await Task.Delay(50);}
            if(window==null)throw new Exception("가상 목록 창이 없다.");
            var observation=(await Request("inspect",new{windowId=window})).GetProperty("observation");File.WriteAllText(Path.Combine(output,"before.json"),observation.GetRawText());
            if(observation.GetProperty("complete").GetBoolean())throw new Exception("가상 목록을 완전 탐색으로 보고했다.");
            var container=observation.GetProperty("nodes").EnumerateArray().Single(n=>n.GetProperty("automationId").GetString()=="VirtualList");
            var foundItem=(await Request("find-item",new{windowId=window,elementId=container.GetProperty("elementId").GetString(),snapshotToken=container.GetProperty("snapshotToken").GetString(),itemName="항목 0999"})).GetProperty("target");
            var mutation=new{windowId=window,elementId=foundItem.GetProperty("elementId").GetString(),snapshotToken=foundItem.GetProperty("snapshotToken").GetString()};
            await Request("realize-item",mutation);await Request("scroll-item-into-view",mutation);
            var after=(await Request("inspect",new{windowId=window})).GetProperty("observation");File.WriteAllText(Path.Combine(output,"after.json"),after.GetRawText());
            if(!after.GetProperty("nodes").EnumerateArray().Any(n=>n.GetProperty("name").GetString()=="항목 0999" && !n.GetProperty("offscreen").GetBoolean()))throw new Exception("실체화 항목을 재조회하지 못했다.");
            if(fakeInput.Events.Count!=0)throw new Exception("전역 입력으로 폴백했다.");
            verdict="PASS";
        }
        catch(Exception e){error=e.Message;}
        finally
        {
            closed=await handler.Release(sessions.Get(session.ControlSessionId),true,"TestEnd");ended=Processes.KillOwned(identity);
            if(!closed||!ended)verdict="FAIL";
            File.WriteAllText(Path.Combine(output,"summary.json"),GuiProtocol.Encode(new{verdict,error,closed,ended,inputEvents=fakeInput.Events.Count,backgroundQualified=false}));
        }
        Console.WriteLine(verdict+" 실제 WPF 가상 목록의 명시적 조회·실체화·스크롤·정리이다. "+output);return verdict=="PASS"?0:1;
    }
}

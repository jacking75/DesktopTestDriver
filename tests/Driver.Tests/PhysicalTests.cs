using DesktopTestDriver;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

static class PhysicalTests
{
    public static async Task<int> Run()
    {
        var root=Path.GetFullPath(Path.Combine("artifacts","physical-"+Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(root);
        var output=Path.Combine(root,"input.json"); var fixtureExe=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","InputWindow","InputWindow.exe"));
        using var probe=new TcpListener(IPAddress.Loopback,32550); probe.Start(); probe.Stop();
        var controller=new ControllerSettings(Path.Combine(root,"controller"),Port:32550);var cs=new StateStore(controller.StateDirectory);ConnectionSettings.Initialize(cs,"controller");
        var agentStore=new StateStore(Path.Combine(root,"agent"));ConnectionSettings.Initialize(agentStore,"physical-local");ConnectionSettings.Register(cs,"physical-local",agentStore.PathFor("identity.cer"));
        using var current=Process.GetCurrentProcess(); using var certificate=ConnectionSettings.Certificate(cs);
        var settings=new AgentSettings(agentStore.Root,"physical-local","127.0.0.1",32550,ConnectionSettings.Hash(certificate),current.SessionId);
        using var desktop=new WindowsDesktop(current.SessionId,Environment.ProcessPath!,agentStore.Root);
        if(!desktop.Valid) throw new GuiException("NoInteractiveSession");
        using var stopController=new CancellationTokenSource();using var stopAgent=new CancellationTokenSource();
        var hosting=Task.Run(()=>new ControllerHost(controller).Run(stopController.Token));
        Task? hostingAgent=null; Process? fixture=null; DesktopProcess? identity=null; string? session=null; bool closed=false,ended=false; string outcome="NOT_RUN"; var receipts=new List<object>();
        async Task<GuiResponse> Call(LocalCommand command) => await ControllerHost.Call(controller,command);
        JsonElement Data(GuiResponse r)=>JsonSerializer.SerializeToElement(r.Data,GuiProtocol.Json);
        async Task<GuiResponse> Request(string operation,object arguments)
        {
            var request=new GuiRequest(2,"physical-"+Guid.NewGuid().ToString("N"),session!,operation,5000,JsonSerializer.SerializeToElement(arguments));
            var began=Stopwatch.GetTimestamp();var response=await Call(new("request",Request:GuiProtocol.Encode(request)));
            receipts.Add(new {request.RequestId,operation,response.Status,response.Error,response.InputEventsSent,milliseconds=Stopwatch.GetElapsedTime(began).TotalMilliseconds});
            if(response.Status!="completed") throw new Exception(GuiProtocol.Encode(response));return response;
        }
        try
        {
            var start=new ProcessStartInfo(fixtureExe){UseShellExecute=false,WindowStyle=ProcessWindowStyle.Hidden};start.ArgumentList.Add(output);fixture=Process.Start(start) ?? throw new Exception("fixture start failed");identity=Processes.Identify(fixture); File.WriteAllText(Path.Combine(root,"owned-fixture.json"),GuiProtocol.Encode(identity));
            var profile=new ApplicationProfile(1,"physical-fixture","windows-uia",new("exe","work","hash"),new(){["counter"]=new(AutomationId:"CounterButton")},new(){["invoke"]=new("counter","invoke")},CaptureAllowed:true);
            new ProfileRegistry(agentStore).Register(GuiProtocol.Encode(profile),new(new(){["exe"]=fixtureExe,["work"]=Path.GetDirectoryName(fixtureExe)!},new(){["hash"]=identity.ImageHash}));
            hostingAgent=Task.Run(()=>new AgentHost(settings,desktop,new WindowsInput()).Run(Environment.ProcessPath!,stopAgent.Token));
            var wait=Stopwatch.StartNew();
            while(wait.Elapsed.TotalSeconds<8)
            {
                var status=await Call(new("status",MachineId:"physical-local"));if(status.Status=="completed") break;await Task.Delay(50);
            }
            // Bounded admission: active human input revokes/refuses the lease; it is never auto-resumed.
            await Task.Delay(2100);
            var opened=await Call(new("session-open",MachineId:"physical-local",ApiVersion:2,InteractionPolicy:"foreground-exclusive",TargetScope:[identity],ApplicationProfileId:"physical-fixture"));
            if(opened.Status!="completed") throw new Exception(GuiProtocol.Encode(opened));session=opened.ControlSessionId;
            var list=Data(await Request("windows",new{})).GetProperty("windows");if(list.GetArrayLength()!=1) throw new Exception("fixture scope not unique");var window=list[0].GetProperty("windowId").GetString()!;
            await Request("focus",new{windowId=window});
            await Request("click",new{windowId=window,automationId="CounterButton"});
            await Request("click",new{windowId=window,automationId="InputText"});
            await Request("type-text",new{windowId=window,text="한글 실제 입력 42"});
            await Request("chord",new{windowId=window,keys=new[]{"CTRL","A"}});
            await Request("type-text",new{windowId=window,text="한글 최종 검증"});
            var confirmation=Stopwatch.StartNew(); bool confirmed=false;
            while(confirmation.Elapsed.TotalSeconds<2)
            {
                using var observed=JsonDocument.Parse(File.ReadAllText(output));
                confirmed=observed.RootElement.GetProperty("text").GetString()=="한글 최종 검증" && observed.RootElement.GetProperty("clicks").GetInt32()==1;
                if(confirmed) break; await Task.Delay(20);
            }
            if(!confirmed) throw new Exception("real fixture input mismatch");
            await Request("capture",new{windowId=window,output=Path.Combine(root,"fixture.png")});
            outcome="PASS";Console.WriteLine("PASS S20 same-machine Controller/Agent real click, Unicode, chord and capture");
        }
        catch(Exception e) {outcome="FAIL";File.WriteAllText(Path.Combine(root,"failure.json"),GuiProtocol.Encode(new{cause="unconfirmed",message=e.Message}));Console.WriteLine("FAIL physical: "+e.Message);}
        finally
        {
            if(session!=null) {try{var response=await Call(new("session-close",SessionId:session));closed=response.Status=="completed";}catch{}}
            stopAgent.Cancel();if(hostingAgent!=null) try{await hostingAgent.WaitAsync(TimeSpan.FromSeconds(6));}catch{}
            stopController.Cancel();try{await hosting.WaitAsync(TimeSpan.FromSeconds(6));}catch{}
            if(identity!=null) ended=Processes.KillOwned(identity);else if(fixture!=null&&!fixture.HasExited){fixture.Kill();ended=fixture.WaitForExit(1500);}fixture?.Dispose();
            var released=!agentStore.Exists("held-input.json") || agentStore.Read<HeldInput[]>("held-input.json").Length==0;
            File.WriteAllText(Path.Combine(root,"summary.json"),GuiProtocol.Encode(new{outcome,identity,closed,ended,inputReleased=released,controllerStopped=hosting.IsCompleted,agentStopped=hostingAgent?.IsCompleted,receipts,humanConcurrentUse="NOT_RUN"}));
            if(!ended || !released || !hosting.IsCompleted || hostingAgent?.IsCompleted==false) outcome="FAIL";
        }
        Console.WriteLine("Evidence: "+root);return outcome=="PASS" ? 0 : 1;
    }
}

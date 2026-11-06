using DesktopTestDriver;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

static class DesktopAcceptanceTests
{
    public static async Task<int> Run(bool concurrent,bool waitForHuman=false)
    {
        var output=Path.GetFullPath(Path.Combine("artifacts",(concurrent?"concurrent-":"comparison-")+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(output);
        using(var port=new TcpListener(IPAddress.Loopback,32550)){port.Start();port.Stop();}
        var exe=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","FormsWorkbench","FormsWorkbench.exe"));
        var processes=new List<(Process Process,ApplicationJob Job,DesktopProcess Identity)>();
        var controller=new ControllerSettings(Path.Combine(output,"controller"),Port:32550);var controllerStore=new StateStore(controller.StateDirectory);ConnectionSettings.Initialize(controllerStore,"controller");
        var agentStore=new StateStore(Path.Combine(output,"agent"));ConnectionSettings.Initialize(agentStore,"acceptance");ConnectionSettings.Register(controllerStore,"acceptance",agentStore.PathFor("identity.cer"));
        using var current=Process.GetCurrentProcess();using var cert=ConnectionSettings.Certificate(controllerStore);
        using var desktop=new WindowsDesktop(current.SessionId,Environment.ProcessPath!,agentStore.Root);
        using var stopController=new CancellationTokenSource();using var stopAgent=new CancellationTokenSource();
        Task? host=null,agent=null;string? session=null;string verdict="FAIL";string? error=null;bool closed=false,inputReleased=false,ended=false;var receipts=new List<JsonElement>();
        object? result=null,failureDiagnostics=null;ForegroundProbe? foreground=null;long externalAtStart=0,hardwareAtStart=0;
        async Task<GuiResponse> Call(LocalCommand command)=>await ControllerHost.Call(controller,command);
        JsonElement Data(GuiResponse value)=>JsonSerializer.SerializeToElement(value.Data,GuiProtocol.Json);
        async Task<JsonElement> Request(string operation,object arguments)
        {
            var request=new GuiRequest(2,Guid.NewGuid().ToString("N"),session!,operation,5000,JsonSerializer.SerializeToElement(arguments));var began=Stopwatch.GetTimestamp();
            var response=await Call(new("request",Request:GuiProtocol.Encode(request)));
            receipts.Add(JsonSerializer.SerializeToElement(new{operation,response.Status,response.Error,response.InputEventsSent,milliseconds=Stopwatch.GetElapsedTime(began).TotalMilliseconds,responseJsonBytes=System.Text.Encoding.UTF8.GetByteCount(GuiProtocol.Encode(response))},GuiProtocol.Json));
            if(response.Status!="completed")throw new Exception(GuiProtocol.Encode(response));return Data(response);
        }
        try
        {
            if(!desktop.Valid)throw new GuiException("NoInteractiveSession");
            for(int i=0;i<1;i++)
            {
                var start=new ProcessStartInfo(exe){UseShellExecute=false};if(concurrent)start.ArgumentList.Add("--quiet");var p=Process.Start(start)!;var job=new ApplicationJob("acceptance-"+Guid.NewGuid().ToString("N"),p);processes.Add((p,job,Processes.Identify(p)));
            }
            File.WriteAllText(Path.Combine(output,"owned.json"),GuiProtocol.Encode(processes.Select(p=>p.Identity).ToArray()));
            var profile=new ApplicationProfile(1,"acceptance","windows-uia",new("exe","work","hash"),new(){["editor"]=new(AutomationId:"Editor"),["apply"]=new(AutomationId:"Apply")},new(){["set"]=new("editor","set-value"),["apply"]=new("apply","invoke")});
            var registry=new ProfileRegistry(agentStore);var registered=registry.Register(GuiProtocol.Encode(profile),new(new(){["exe"]=exe,["work"]=Path.GetDirectoryName(exe)!},new(){["hash"]=processes[0].Identity.ImageHash}));
            if(concurrent)
            {
                // The qualification probe is restricted to this owned fixture; it sends no OS input.
                using var probeForeground=new ForegroundProbe(processes[0].Identity.Pid);
                var probeStore=new StateStore(Path.Combine(output,"probe"));var sessions=new ControlSessions(probeStore,"probe",current.SessionId,"dedicated");var info=sessions.Open("control",120,2,"isolated-desktop",[processes[0].Identity]);var fake=new FakeInput();var handler=new GuiRequestHandler(probeStore,desktop,sessions,new(probeStore,fake),machineUsage:"dedicated");
                async Task<JsonElement> Probe(string op,object arguments)
                {
                    var req=GuiProtocol.Parse(GuiProtocol.Encode(new{apiVersion=2,requestId=Guid.NewGuid().ToString("N"),controlSessionId=info.ControlSessionId,operation=op,arguments,timeoutMilliseconds=5000}));var response=await handler.Execute(new(req,5000));if(response.Status!="completed")throw new Exception(GuiProtocol.Encode(response));return Data(response);
                }
                try
                {
                    var window=(await Probe("wait-window",new{title="독립 GUI 검증 도구"})).GetProperty("window").GetProperty("windowId").GetString()!;
                    var editor=(await Probe("find-element",new{windowId=window,selector=new{automationId="Editor"}})).GetProperty("target");
                    await Probe("set-value",new{windowId=window,elementId=editor.GetProperty("elementId").GetString(),snapshotToken=editor.GetProperty("snapshotToken").GetString(),value="사전 검증"});
                    var button=(await Probe("find-element",new{windowId=window,selector=new{automationId="Apply"}})).GetProperty("target");await Probe("invoke",new{windowId=window,elementId=button.GetProperty("elementId").GetString(),snapshotToken=button.GetProperty("snapshotToken").GetString()});
                    await Probe("wait-condition",new{windowId=window,selector=new{automationId="Result"},condition="value-equals",expected="사전 검증"});
                    if(probeForeground.Count!=0||fake.Events.Count!=0)throw new Exception("동시 작업 전 qualification 실패이다.");
                    var evidence=Path.Combine(output,"qualification-evidence.json");File.WriteAllText(evidence,GuiProtocol.Encode(new{globalInputEvents=fake.Events.Count,targetForegroundEvents=probeForeground.Count}));
                    registry.Qualify(new("acceptance",registered.ProfileHash,registered.BinaryHashes,Environment.OSVersion.VersionString,[new("editor","set-value"),new("apply","invoke")],DateTimeOffset.UtcNow,evidence,GuiProtocol.Hash(File.ReadAllBytes(evidence)),0,0,true));
                }
                finally{await handler.Release(sessions.Get(info.ControlSessionId),true,"ProbeEnd");}
            }
            host=Task.Run(()=>new ControllerHost(controller).Run(stopController.Token));
            var settings=new AgentSettings(agentStore.Root,"acceptance","127.0.0.1",32550,ConnectionSettings.Hash(cert),current.SessionId,MachineUsage:concurrent?"shared":"dedicated");
            agent=Task.Run(()=>new AgentHost(settings,desktop,new WindowsInput()).Run(Environment.ProcessPath!,stopAgent.Token));
            var timer=Stopwatch.StartNew();while(timer.Elapsed.TotalSeconds<8){if((await Call(new("status",MachineId:"acceptance"))).Status=="completed")break;await Task.Delay(50);}
            if(!concurrent)await Task.Delay(2100);
            var opened=await Call(new("session-open",MachineId:"acceptance",ApiVersion:2,InteractionPolicy:concurrent?"background-only":"isolated-desktop",TargetScope:processes.Select(p=>p.Identity).ToArray(),ApplicationProfileId:"acceptance"));if(opened.Status!="completed")throw new Exception(GuiProtocol.Encode(opened));session=opened.ControlSessionId;
            var admissionStatus=Data(await Call(new("status",MachineId:"acceptance")));externalAtStart=admissionStatus.GetProperty("externalInputEvents").GetInt64();hardwareAtStart=admissionStatus.GetProperty("hardwareInputEvents").GetInt64();
            var windows=(await Request("windows",new{})).GetProperty("windows");
            string Window(int index)=>windows.EnumerateArray().Single(w=>w.GetProperty("pid").GetInt32()==processes[index].Identity.Pid).GetProperty("windowId").GetString()!;
            async Task Semantic(string window,string value)
            {
                var editor=(await Request("find-element",new{windowId=window,selector=new{automationId="Editor"}})).GetProperty("target");await Request("set-value",new{windowId=window,elementId=editor.GetProperty("elementId").GetString(),snapshotToken=editor.GetProperty("snapshotToken").GetString(),value});
                var button=(await Request("find-element",new{windowId=window,selector=new{automationId="Apply"}})).GetProperty("target");await Request("invoke",new{windowId=window,elementId=button.GetProperty("elementId").GetString(),snapshotToken=button.GetProperty("snapshotToken").GetString()});
                await Request("wait-condition",new{windowId=window,selector=new{automationId="Result"},condition="value-equals",expected=value});
            }
            if(concurrent)
            {
                foreground=new ForegroundProbe(processes[0].Identity.Pid);
                File.WriteAllText(Path.Combine(output,"ready.json"),GuiProtocol.Encode(new{readyAt=DateTimeOffset.UtcNow,measurementSeconds=40,startFile=Path.Combine(output,"start.request")}));
                Console.WriteLine("READY "+output);
                if(waitForHuman)
                {
                    var readiness=Stopwatch.StartNew();
                    while(!File.Exists(Path.Combine(output,"start.request")))
                    {
                        if(readiness.Elapsed.TotalSeconds>90)throw new Exception("HumanReadinessNotReceived");
                        await Task.Delay(100);
                    }
                }
                var initial=Data(await Call(new("status",MachineId:"acceptance")));
                var before=initial.GetProperty("externalInputEvents").GetInt64();var hardwareBefore=initial.GetProperty("hardwareInputEvents").GetInt64();
                var measurementStarted=DateTimeOffset.UtcNow;
                timer.Restart();int iterations=0,afterActivity=0;long external=0,hardware=0;
                while(timer.Elapsed.TotalSeconds<40)
                {
                    await Semantic(Window(0),"병행 "+iterations++);
                    var status=Data(await Call(new("status",MachineId:"acceptance")));external=status.GetProperty("externalInputEvents").GetInt64()-before;hardware=status.GetProperty("hardwareInputEvents").GetInt64()-hardwareBefore;
                    if(external>0 && ++afterActivity>=3)break;await Task.Delay(100);
                }
                result=new{iterations,measurementStarted,measurementMilliseconds=timer.Elapsed.TotalMilliseconds,externalNonDriverInputEvents=external,hardwareInputEvents=hardware,targetForegroundEvents=foreground.Count,driverInputEvents=receipts.Sum(r=>r.GetProperty("inputEventsSent").GetInt32()),sessionKind=WindowsDesktop.SessionKind(current.SessionId)};
                if(foreground.Count!=0||receipts.Any(r=>r.GetProperty("inputEventsSent").GetInt32()!=0))throw new Exception("동시 작업 중 간섭을 관측했다.");
                verdict=external>0?"PASS":"NOT_RUN";
            }
            else
            {
                // 속도 비교는 같은 프로세스·같은 전경 창 조건으로 맞춘다. 공유 화면 비간섭은 별도 concurrent 검증이다.
                var baseline=Window(0);var semantic=baseline;await Request("focus",new{windowId=baseline});foreground=new ForegroundProbe(processes[0].Identity.Pid);await Task.Delay(50);var initialForeground=foreground.Count;
                async Task Physical(string value)
                {
                    await Request("focus",new{windowId=baseline});
                    await Request("click",new{windowId=baseline,automationId="Editor"});await Request("chord",new{windowId=baseline,keys=new[]{"CTRL","A"}});await Request("type-text",new{windowId=baseline,text=value});await Request("click",new{windowId=baseline,automationId="Apply"});await Request("wait-condition",new{windowId=baseline,selector=new{automationId="Result"},condition="value-equals",expected=value});
                }
                // 같은 창에서 각 경로를 한 번 예열하고 측정에서 제외한다.
                await Physical("비교 예열");await Semantic(semantic,"비교 예열");
                var samples=new List<object>();
                async Task<object> Measure(Func<Task> operation)
                {
                    int first=receipts.Count;var measured=Stopwatch.StartNew();await operation();var batch=receipts.Skip(first).ToArray();
                    return new{milliseconds=measured.Elapsed.TotalMilliseconds,commands=batch.Length,responseJsonBytes=batch.Sum(r=>r.GetProperty("responseJsonBytes").GetInt32()),inputEvents=batch.Sum(r=>r.GetProperty("inputEventsSent").GetInt32()),imageBytes=0,success=true};
                }
                for(int i=0;i<3;i++)
                {
                    var value="동일 비교 한글 "+i;object input,uia;
                    // 순서 편향을 줄이되 같은 창·같은 값·같은 성공 조건을 유지한다.
                    if(i%2==0){input=await Measure(()=>Physical(value));uia=await Measure(()=>Semantic(semantic,value));}
                    else{uia=await Measure(()=>Semantic(semantic,value));input=await Measure(()=>Physical(value));}
                    samples.Add(new{iteration=i,order=i%2==0?"input-then-uia":"uia-then-input",input,uia});
                }
                result=new{samples,warmupPerPath=1,measuredSamplesPerPath=3,targetForegroundEvents=foreground.Count-initialForeground,initialForegroundEntries=initialForeground,fixtureInstances=1,comparisonCondition="same-foreground-window",fixtureHash=processes[0].Identity.ImageHash,setupExcluded=true,screenCaptureRequested=false};
                var semanticReceipts=receipts.Where(r=>r.GetProperty("operation").GetString() is "set-value" or "invoke");
                if(foreground.Count!=initialForeground||semanticReceipts.Any(r=>r.GetProperty("inputEventsSent").GetInt32()!=0))throw new Exception("비교 측정 중 예상하지 않은 전경 변경 또는 UIA 전역 입력을 관측했다.");verdict="PASS";
            }
        }
        catch(Exception e)
        {
            error=e.Message;
            if(agent?.IsCompleted==false&&host?.IsCompleted==false)try
            {
                var status=Data(await Call(new("status",MachineId:"acceptance")));
                failureDiagnostics=new{activityMonitorState=status.GetProperty("activityMonitorState").GetString(),externalInputsSinceSessionOpen=status.GetProperty("externalInputEvents").GetInt64()-externalAtStart,hardwareInputsSinceSessionOpen=status.GetProperty("hardwareInputEvents").GetInt64()-hardwareAtStart};
            }catch{}
        }
        finally
        {
            foreground?.Dispose();
            if(session!=null){try{closed=(await Call(new("session-close",SessionId:session))).Status=="completed";}catch{}}
            stopAgent.Cancel();if(agent!=null)try{await agent.WaitAsync(TimeSpan.FromSeconds(6));}catch{}
            stopController.Cancel();if(host!=null)try{await host.WaitAsync(TimeSpan.FromSeconds(6));}catch{}
            inputReleased=!agentStore.Exists("held-input.json")||agentStore.Read<HeldInput[]>("held-input.json").Length==0;
            ended=true;foreach(var p in processes){ended &= Processes.KillOwned(p.Identity);p.Job.Dispose();p.Process.Dispose();}
            if(!ended||!inputReleased||agent?.IsCompleted==false||host?.IsCompleted==false)verdict="FAIL";
            File.WriteAllText(Path.Combine(output,"summary.json"),GuiProtocol.Encode(new{verdict,error,result,failureDiagnostics,targetForegroundEvents=foreground?.Count,closed,inputReleased,ended,agentStopped=agent?.IsCompleted,controllerStopped=host?.IsCompleted,receipts}));
        }
        Console.WriteLine(verdict+" "+(concurrent?"실제 동시 작업":"동일 fixture 입력/UIA 비교")+". "+output);return verdict=="PASS"?0:verdict=="NOT_RUN"?2:1;
    }
}

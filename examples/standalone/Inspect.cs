using DesktopTestDriver;
using System.IO;
using System.Text.Json;

// dotnet run --project examples/standalone -- Controller.json machine target-scope.json
if (args.Length != 3) throw new ArgumentException("Controller 설정·machine·scope 파일을 지정한다.");
var client = new DesktopClient(ConnectionSettings.Controller(args[0]));
var scope = GuiProtocol.Decode<DesktopProcess[]>(File.ReadAllText(args[2]));
var opened = await client.Open(args[1], scope);
if (opened.Status != "completed") throw new Exception(opened.Error?.Code);
try
{
    var windows = await client.Execute(opened.ControlSessionId!, "windows", new { });
    if (windows.Status != "completed") throw new Exception(windows.Error?.Code);
    foreach (var window in JsonSerializer.SerializeToElement(windows.Data, GuiProtocol.Json).GetProperty("windows").EnumerateArray())
        Console.WriteLine(GuiProtocol.Encode(await client.Execute(opened.ControlSessionId!, "inspect", new { windowId = window.GetProperty("windowId").GetString() })));
}
finally
{
    var closed = await client.Close(opened.ControlSessionId!);
    if (closed.Status != "completed") throw new Exception("세션 정리 상태 확인이 필요하다.");
}

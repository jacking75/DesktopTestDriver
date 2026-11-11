using DesktopTestDriver;

// A complete replayable flow without a model: find by selector -> set a value -> wait for a condition -> verify.
// dotnet run --project examples/standalone -- CONTROLLER_STATE_DIR MACHINE PATH\TO\FormsWorkbench.exe
if (args.Length != 3) throw new ArgumentException("Controller state folder, machine id and the FormsWorkbench.exe path are required.");
var client = new DesktopClient(new ControllerSettings(args[0]));
var opened = (await client.OpenSessionAsync(args[1], launch: args[2], launchArguments: ["--quiet"])).EnsureCompleted();
var session = opened.ControlSessionId();
try
{
    var window = opened.Json().GetProperty("launch").GetProperty("window").GetProperty("windowId").GetString()!;
    var editor = (await client.FindElementAsync(session, window, new(AutomationId: "Editor"))).EnsureCompleted().Ref(window);
    (await client.SetValueAsync(session, editor, "SDK 예제")).EnsureCompleted();
    var apply = (await client.FindElementAsync(session, window, new(AutomationId: "Apply"))).EnsureCompleted().Ref(window);
    (await client.InvokeAsync(session, apply)).EnsureCompleted();
    var waited = (await client.WaitAsync(session, window, new(AutomationId: "Result"), "value-equals", "SDK 예제")).EnsureCompleted();
    if (!waited.Satisfied()) throw new Exception("Result did not change");
    Console.WriteLine((await client.InspectAsync(session, window, maxDepth: 3, interactiveOnly: true)).EnsureCompleted().Tree());
    Console.WriteLine("verified: Result = SDK 예제");
}
finally
{
    var closed = await client.CloseSessionAsync(session);
    if (closed.Status != "completed") throw new Exception("Session cleanup needs checking: " + closed.Error?.Code);
}

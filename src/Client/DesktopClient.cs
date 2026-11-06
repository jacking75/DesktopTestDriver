using System.Text.Json;

namespace DesktopTestDriver;
public sealed class DesktopClient(ControllerSettings settings)
{
    public Task<GuiResponse> OpenSessionAsync(string machine, DesktopProcess[] targets, string mode = "observe", string policy = "background-only", CancellationToken cancellation = default, string? applicationProfileId = null) => Open(machine, targets, mode, policy, applicationProfileId).WaitAsync(cancellation);
    public Task<GuiResponse> GetCapabilitiesAsync(string session, string window, CancellationToken cancellation = default) => Execute(session, "capabilities", new { windowId = window }, cancellation);
    public Task<GuiResponse> FindElementAsync(string session, string window, ElementSelector selector, CancellationToken cancellation = default) => Execute(session, "find-element", new { windowId = window, selector = System.Text.Json.JsonSerializer.SerializeToElement(selector, new JsonSerializerOptions(GuiProtocol.Json) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }) }, cancellation);
    public Task<GuiResponse> SetValueAsync(string session, string window, string element, string snapshotToken, string value, string requestId, CancellationToken cancellation = default) => Execute(session, "set-value", new { windowId = window, elementId = element, snapshotToken, value }, cancellation, requestId);
    public Task<GuiResponse> InvokeAsync(string session, string window, string element, string snapshotToken, string requestId, CancellationToken cancellation = default) => Execute(session, "invoke", new { windowId = window, elementId = element, snapshotToken }, cancellation, requestId);
    public Task<GuiResponse> WaitAsync(string session, string window, ElementSelector selector, string condition, string? expected, int timeoutMilliseconds = 5000, CancellationToken cancellation = default) => Execute(session, "wait-condition", new { windowId = window, selector = System.Text.Json.JsonSerializer.SerializeToElement(selector, new JsonSerializerOptions(GuiProtocol.Json) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }), condition, expected }, cancellation, timeoutMilliseconds: timeoutMilliseconds);
    public Task<GuiResponse> GetRequestResultAsync(string session, string requestId, CancellationToken cancellation = default) => Result(session, requestId).WaitAsync(cancellation);
    public Task<GuiResponse> CloseSessionAsync(string session, CancellationToken cancellation = default) => Close(session).WaitAsync(cancellation);
    public Task<GuiResponse> Open(string machine, DesktopProcess[] targets, string mode = "observe", string policy = "background-only", string? applicationProfileId = null) =>
        ControllerHost.Call(settings, new("session-open", MachineId: machine, Mode: mode, ApiVersion: 2, InteractionPolicy: policy, TargetScope: targets, ApplicationProfileId: applicationProfileId));
    public async Task<GuiResponse> Execute(string session, string operation, object arguments, CancellationToken cancellation = default, string? requestId = null, int timeoutMilliseconds = 5000)
    {
        cancellation.ThrowIfCancellationRequested();
        var request = new GuiRequest(2, requestId ?? "r-" + Guid.NewGuid().ToString("N"), session, operation, timeoutMilliseconds, JsonSerializer.SerializeToElement(arguments, GuiProtocol.Json));
        var json = GuiProtocol.Encode(request); GuiProtocol.Parse(json);
        // Cancellation stops waiting; it never authorizes replay of an uncertain mutation.
        try { return await ControllerHost.Call(settings, new("request", Request: json)).WaitAsync(cancellation); }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested)
        {
            return new(GuiProtocol.ReadOnly(operation) ? "running" : "completionUnknown",request.RequestId,session,
                Data:new {effectState=GuiProtocol.ReadOnly(operation) ? "none" : "unknown",inputCountKnown=false},Error:new("ClientWaitCanceled","ClientWaitCanceled")) {ApiVersion=2,Operation=operation};
        }
    }
    public Task<GuiResponse> Result(string session, string requestId) => ControllerHost.Call(settings, new("request-status", SessionId: session, RequestId: requestId));
    public Task<GuiResponse> Close(string session) => ControllerHost.Call(settings, new("session-close", SessionId: session));
    public Task<GuiResponse> Pause(string session) => ControllerHost.Call(settings, new("session-pause", SessionId: session));
    public Task<GuiResponse> Resume(string session, long expectedRevision) => ControllerHost.Call(settings, new("session-resume", SessionId: session, ExpectedRevision: expectedRevision));
}

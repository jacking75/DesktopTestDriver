namespace DesktopTestDriver;
public static class OperationCatalog
{
    public static OperationDescriptor[] All => GuiProtocol.Operations.Select(operation => new OperationDescriptor(operation,
        operation == "capture" ? "windows-capture" : operation is "windows" or "controls" ? "windows-observation" : operation is "launch" or "terminate-owned" or "close-window" ? "windows-process" : "windows-input",
        GuiProtocol.ReadOnly(operation), [],
        MaySendGlobalInput: operation is "click" or "type-text" or "key" or "chord" or "move" or "wheel" or "drag" or "release-input",
        MayActivateWindow: operation is "focus" or "launch" or "close-window",
        MayOpenWindow: operation is "launch" or "close-window",
        MayChangeView: !GuiProtocol.ReadOnly(operation))).Concat(AutomationOperations.All.Select(d => d.ReadOnly ? d : d with { MayActivateWindow = true })).ToArray();
}

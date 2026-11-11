using System.Globalization;
namespace DesktopTestDriver;
/// <summary>Helper process entry points: the worker (UIA, capture, focus) and the watchdog.</summary>
public static class InternalDesktopCommands
{
    public static int? TryRun(string[] args)
    {
        if (args is ["--worker", var pipe, var parent]) return WorkerProcess.Run(pipe, int.Parse(parent, CultureInfo.InvariantCulture));
        if (args is ["--watchdog", var root]) return WatchdogRunner.Run(root);
        if (args.Length > 0 && args[0] is "--worker" or "--watchdog") throw new GuiException("InvalidInternalCommand");
        return null;
    }
}

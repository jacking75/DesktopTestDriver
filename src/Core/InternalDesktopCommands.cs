using System.Globalization;
namespace DesktopTestDriver;
public static class InternalDesktopCommands
{
    public static async Task<int?> TryRun(string[] args)
    {
        long L(string value)=>long.Parse(value,CultureInfo.InvariantCulture);
        int I(string value)=>int.Parse(value,CultureInfo.InvariantCulture);
        if(args is ["--uia-worker",var pipe,var parent,var start]) return UiaWorker.Run(pipe,I(parent),L(start),Environment.ProcessPath!);
        if(args is ["--uia",var handle]) {Console.WriteLine(GuiProtocol.Encode(await Task.Run(()=>WindowsDesktop.ReadControls(L(handle)))));return 0;}
        if(args is ["--focus",var focus,var pid,var created]) return WindowsDesktop.ActivateWindow(L(focus),I(pid),L(created)) ? 0 : 1;
        if(args is ["--capture",var capture,var capturePid,var captureStart,var output,var x,var y,var width,var height]) {await GraphicsCapture.Save(L(capture),I(capturePid),L(captureStart),new(I(x),I(y),I(width),I(height)),output);return 0;}
        if(args is ["--watchdog",var root]) return await WatchdogRunner.Run(root);
        if(args.Length>0 && args[0] is "--uia-worker" or "--uia" or "--focus" or "--capture" or "--watchdog") throw new GuiException("InvalidInternalCommand");
        return null;
    }
}

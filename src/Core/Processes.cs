using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
namespace DesktopTestDriver;

public static class Processes
{
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool QueryFullProcessImageName(IntPtr process,int flags,StringBuilder path,ref int size);
    public static DesktopProcess Identify(Process process)
    {
        // A newly spawned process can exist before its main module becomes queryable.
        var began = Stopwatch.GetTimestamp();
        do
        {
            process.Refresh();
            var buffer=new StringBuilder(32768); var size=buffer.Capacity;
            if (QueryFullProcessImageName(process.Handle,0,buffer,ref size))
            {
                var path=Path.GetFullPath(buffer.ToString());
                return new(process.Id, process.StartTime.ToUniversalTime(), path, GuiProtocol.Hash(File.ReadAllBytes(path)));
            }
            if (process.HasExited) break;
            Thread.Sleep(5);
        } while (Stopwatch.GetElapsedTime(began).TotalMilliseconds < 500);
        throw new GuiException("ObservationUnavailable");
    }
    public static bool Alive(DesktopProcess identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.Pid);
            return Identify(process) == identity && !process.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { return false; }
    }
    public static bool KillOwned(DesktopProcess identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.Pid);
            if (process.HasExited) return true;
            if (Identify(process) != identity) return false;
            process.Kill(false);
            return process.WaitForExit(1500);
        }
        catch (ArgumentException) { return true; }
        catch { return false; }
    }
    public static Process Start(string executable, params string[] args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new GuiException("ObservationUnavailable");
    }
}

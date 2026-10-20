using System.Diagnostics;
namespace DesktopTestDriver;

public static class Processes
{
    public static DesktopProcess Identify(Process process)
    {
        // A newly spawned process can exist before its main module becomes queryable.
        var began = Stopwatch.GetTimestamp();
        do
        {
            process.Refresh();
            var path = process.MainModule?.FileName;
            if (path != null) return new(process.Id, process.StartTime.ToUniversalTime(), Path.GetFullPath(path), GuiProtocol.Hash(File.ReadAllBytes(path)));
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

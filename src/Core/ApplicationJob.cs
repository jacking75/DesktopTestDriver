using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DesktopTestDriver;
public sealed class ApplicationJob : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct Limits { public long PerProcess, PerJob; public uint Flags; public UIntPtr Min, Max; public uint Active; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct Io { public ulong Read, Write, Other, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct Extended { public Limits Basic; public Io Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle OpenJobObject(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref Extended info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(SafeFileHandle job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(SafeFileHandle job, int type, IntPtr data, uint size, out uint returned);
    private readonly SafeFileHandle handle;
    public string Name { get; }
    public ApplicationJob(string id, Process process)
    {
        Name = "Local\\DesktopTestDriver-job-" + id;
        handle = CreateJobObject(IntPtr.Zero, Name);
        if (handle.IsInvalid) throw new GuiException("ApplicationJobUnavailable");
        var limits = new Extended { Basic = new Limits { Flags = 0x2000 } };
        if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<Extended>()) || !AssignProcessToJobObject(handle, process.Handle)) { handle.Dispose(); throw new GuiException("OwnershipUnconfirmed"); }
    }
    public DesktopProcess[] Members()
    {
        const int size = 8 + 8 * 1024; var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(handle, 3, buffer, size, out _)) throw new GuiException("OwnershipUnconfirmed");
            var count = Marshal.ReadInt32(buffer, 4); if (count is < 0 or > 1024) throw new GuiException("OwnershipUnconfirmed");
            var members = new List<DesktopProcess>();
            for (int i = 0; i < count; i++) try { using var p = Process.GetProcessById(checked((int)Marshal.ReadInt64(buffer, 8 + 8 * i))); members.Add(Processes.Identify(p)); } catch (ArgumentException) { }
            return members.ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public static bool Stop(string name)
    {
        using var job = OpenJobObject(12, false, name);
        if (job.IsInvalid) return Marshal.GetLastWin32Error() == 2;
        if (!TerminateJobObject(job, 1)) return false;
        var began = Stopwatch.GetTimestamp(); var buffer = Marshal.AllocHGlobal(8 + 8 * 1024);
        try
        {
            do
            {
                if (!QueryInformationJobObject(job, 3, buffer, 8 + 8 * 1024, out _)) return false;
                if (Marshal.ReadInt32(buffer, 4) == 0) return true;
                Thread.Sleep(10);
            } while (Stopwatch.GetElapsedTime(began).TotalMilliseconds < 1500);
            return false;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public void Dispose() => handle.Dispose();
}

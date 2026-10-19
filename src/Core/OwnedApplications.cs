using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DesktopTestDriver;

// Atomic job-list assignment at CreateProcess prevents an unowned child if Client crashes during launch.
public sealed class OwnedApplications : IDisposable
{
    private SafeFileHandle? job;
    public ProcessIdentity Launch(RunStore store, string id, Action checkLive)
    {
        if (!store.Config.AllowInput || File.Exists(store.PathFor("input-sealed.json"))) throw new DriverException(Error.DelegationMismatch);
        var app = store.Config.Applications?.SingleOrDefault(a => a.Id == id) ?? throw new DriverException(Error.DelegationMismatch);
        for (FileSystemInfo? item = new FileInfo(app.ImagePath); item != null; item = item is FileInfo f ? f.Directory : ((DirectoryInfo)item).Parent)
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new DriverException(Error.DelegationMismatch);
        using var image = new FileStream(app.ImagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(image)) != app.ImageHash) throw new DriverException(Error.DelegationMismatch);
        checkLive();
        using var held = store.Lock();
        if (store.Stopped) throw new DriverException(Error.Stopped);
        var ledger = store.Read<InputLedger>("ledger.json");
        if (ledger.Processes.Length >= 16) throw new DriverException(Error.InvalidRequest);
        if (job == null)
        {
            var name = "Local\\DTD-apps-" + Guid.NewGuid().ToString("N");
            job = Native.CreateJobObject(IntPtr.Zero, name);
            if (job.IsInvalid || Marshal.GetLastWin32Error() == 183) throw new Win32Exception();
            var limits = new Native.ExtendedLimit { Basic = new Native.BasicLimit { LimitFlags = 0x2000 } };
            if (!Native.SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<Native.ExtendedLimit>())) throw new Win32Exception();
            store.Write("application-job.json", new JobReceipt(name));
        }
        nuint size = 0;
        Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var attributes = Marshal.AllocHGlobal(checked((int)size));
        var jobList = Marshal.AllocHGlobal(IntPtr.Size);
        bool initialized = false;
        Native.ProcessInfo process = default;
        try
        {
            if (!Native.InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception();
            initialized = true;
            Marshal.WriteIntPtr(jobList, job.DangerousGetHandle());
            if (!Native.UpdateProcThreadAttribute(attributes, 0, (nuint)0x2000D, jobList, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
            var startup = new Native.StartupEx { Startup = new Native.Startup { Size = Marshal.SizeOf<Native.StartupEx>() }, Attributes = attributes };
            var commandLine = new StringBuilder(string.Join(" ", new[] { app.ImagePath }.Concat(app.Arguments).Select(Quote)));
            if (!Native.CreateProcess(app.ImagePath, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0x00080004, IntPtr.Zero,
                Path.GetDirectoryName(app.ImagePath), ref startup, out process)) throw new Win32Exception();
            using var child = Process.GetProcessById(process.Pid);
            var path = new StringBuilder(32768); var count = path.Capacity;
            if (!Native.QueryFullProcessImageName(process.Process, 0, path, ref count) || !path.ToString().Equals(app.ImagePath, StringComparison.OrdinalIgnoreCase))
                throw new DriverException(Error.DelegationMismatch);
            var identity = new ProcessIdentity(process.Pid, child.StartTime.ToUniversalTime(), Path.GetFullPath(app.ImagePath), app.ImageHash);
            store.Write("ledger.json", ledger with { Processes = [.. ledger.Processes, new(identity)] });
            if (Native.ResumeThread(process.Thread) == uint.MaxValue) throw new Win32Exception();
            return identity;
        }
        catch { if (process.Process != IntPtr.Zero) Native.TerminateProcess(process.Process, 1); throw; }
        finally
        {
            if (process.Thread != IntPtr.Zero) Native.CloseHandle(process.Thread);
            if (process.Process != IntPtr.Zero) Native.CloseHandle(process.Process);
            if (initialized) Native.DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes); Marshal.FreeHGlobal(jobList);
        }
    }
    public static bool Terminate(RunStore store)
    {
        if (!File.Exists(store.PathFor("application-job.json"))) return true;
        var name = store.Read<JobReceipt>("application-job.json").Name;
        if (!name.StartsWith("Local\\DTD-apps-", StringComparison.Ordinal)) return false;
        using var handle = Native.OpenJobObject(8, false, name);
        if (handle.IsInvalid) return Marshal.GetLastWin32Error() == 2;
        return Native.TerminateJobObject(handle, 1);
    }
    public static string Quote(string value)
    {
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); result.Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    public void Dispose() { job?.Dispose(); job = null; }
    private sealed record JobReceipt(string Name);
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct BasicLimit { public long PerProcess, PerJob; public uint LimitFlags; public nuint MinWorking, MaxWorking; public uint ActiveLimit; public nuint Affinity; public uint Priority, Scheduling; }
        [StructLayout(LayoutKind.Sequential)] internal struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] internal struct ExtendedLimit { public BasicLimit Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcess, PeakJob; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct Startup { public int Size; public string? Reserved, Desktop, Title; public uint X,Y,XSize,YSize,XCount,YCount,Fill,Flags; public ushort Show,ReservedSize; public IntPtr ReservedPtr,Input,Output,Error; }
        [StructLayout(LayoutKind.Sequential)] internal struct StartupEx { public Startup Startup; public IntPtr Attributes; }
        [StructLayout(LayoutKind.Sequential)] internal struct ProcessInfo { public IntPtr Process,Thread; public int Pid,Tid; }
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern SafeFileHandle OpenJobObject(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimit limits, int size);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool TerminateJobObject(SafeFileHandle job, uint code);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", EntryPoint="CreateProcessW", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern bool CreateProcess(string app, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string? directory, ref StartupEx startup, out ProcessInfo process);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int length);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll")] internal static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
    }
}

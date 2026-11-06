using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopTestDriver;

internal sealed class SuspendedApplication : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] private struct Startup { public int Size; public string? Reserved, Desktop, Title; public int X,Y,Width,Height,XCount,YCount,Fill,Flags; public short Show,ReservedBytes; public IntPtr ReservedPointer,Input,Output,Error; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process,Thread; public int Pid,Tid; }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool CreateProcess(string application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,bool inherit,uint flags,IntPtr environment,string directory,ref Startup startup,out ProcessInfo info);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern uint ResumeThread(IntPtr thread);
    private ProcessInfo info;
    public Process Process { get; }
    public SuspendedApplication(string executable,string[] arguments,string directory)
    {
        var startup=new Startup {Size=Marshal.SizeOf<Startup>()};
        var command=new StringBuilder(string.Join(" ",new[]{executable}.Concat(arguments).Select(Quote)));
        if(!CreateProcess(executable,command,IntPtr.Zero,IntPtr.Zero,false,4,IntPtr.Zero,directory,ref startup,out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        Process=Process.GetProcessById(info.Pid);
    }
    public void Resume() { if(ResumeThread(info.Thread)==uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    private static string Quote(string value)
    {
        var result=new StringBuilder("\""); int slashes=0;
        foreach(var c in value)
        {
            if(c=='\\') {slashes++;continue;}
            result.Append('\\',c=='"' ? slashes*2+1 : slashes); result.Append(c); slashes=0;
        }
        return result.Append('\\',slashes*2).Append('"').ToString();
    }
    public void Dispose() { Native.CloseHandle(info.Thread); Native.CloseHandle(info.Process); Process.Dispose(); }
}

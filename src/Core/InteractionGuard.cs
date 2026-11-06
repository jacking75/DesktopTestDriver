using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace DesktopTestDriver;

// Only counters and health are retained. Keys, pointer positions and text are never recorded.
public interface IActivityMonitor : IDisposable
{
    long Sequence {get;}
    bool Healthy {get;}
    bool Idle {get;}
    long LastActivityForProcess(int pid);
    long ForegroundSequence => 0;
    long LastForegroundForProcess(int pid) => 0;
    int CurrentForegroundProcess => 0;
    void Ping();
}
public sealed class ActivityMonitor : IActivityMonitor
{
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready = new();
    private uint threadId;
    private volatile bool healthy;
    private long sequence, lastActivity = Stopwatch.GetTimestamp(), lastPump;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int,long> processEpochs=new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int,long> foregroundEpochs=new();
    private long foregroundSequence,hardwareInputs;
    public long HardwareInputs => Interlocked.Read(ref hardwareInputs);
    public long ForegroundSequence => Interlocked.Read(ref foregroundSequence);
    public long LastForegroundForProcess(int pid) => foregroundEpochs.GetValueOrDefault(pid);
    public int CurrentForegroundProcess {get {Native.GetWindowThreadProcessId(Native.GetForegroundWindow(),out var pid);return (int)pid;}}
    public long LastActivityForProcess(int pid) => processEpochs.GetValueOrDefault(pid);
    public long Sequence => Interlocked.Read(ref sequence);
    public bool Healthy => healthy && Stopwatch.GetElapsedTime(Interlocked.Read(ref lastPump)).TotalSeconds < 2;
    public bool Idle => Stopwatch.GetElapsedTime(Interlocked.Read(ref lastActivity)).TotalSeconds >= 2;
    public ActivityMonitor()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "DesktopTestDriver activity monitor" };
        thread.Start(); if (!ready.Wait(2000)) throw new GuiException("ActivityMonitorUnavailable");
    }
    public void Ping() { if (threadId != 0) Native.PostThreadMessage(threadId, 0x8001, UIntPtr.Zero, IntPtr.Zero); }
    private void Run()
    {
        threadId = Native.GetCurrentThreadId(); _ = Native.PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        IntPtr keyboard = IntPtr.Zero, mouse = IntPtr.Zero,foreground=IntPtr.Zero;
        void Activity() { var epoch=Interlocked.Increment(ref sequence); Interlocked.Exchange(ref lastActivity, Stopwatch.GetTimestamp()); Native.GetWindowThreadProcessId(Native.GetForegroundWindow(),out var pid); processEpochs[(int)pid]=epoch; }
        Native.HookCallback key = (code, message, data) => { if (code >= 0) {var input=Marshal.PtrToStructure<Native.KeyHook>(data);if(input.Extra!=WindowsInput.Marker) {Activity();if((input.Flags&0x10)==0)Interlocked.Increment(ref hardwareInputs);}} return Native.CallNextHookEx(IntPtr.Zero, code, message, data); };
        Native.HookCallback pointer = (code, message, data) => { if (code >= 0) {var input=Marshal.PtrToStructure<Native.MouseHook>(data);if(input.Extra!=WindowsInput.Marker) {Activity();if((input.Flags&1)==0)Interlocked.Increment(ref hardwareInputs);}} return Native.CallNextHookEx(IntPtr.Zero, code, message, data); };
        Native.WinEventCallback activated=(_,_,window,_,_,_,_)=>{Native.GetWindowThreadProcessId(window,out var pid);foregroundEpochs[(int)pid]=Interlocked.Increment(ref foregroundSequence);};
        try
        {
            keyboard = Native.SetWindowsHookEx(13, key, IntPtr.Zero, 0); mouse = Native.SetWindowsHookEx(14, pointer, IntPtr.Zero, 0);
            foreground=Native.SetWinEventHook(3,3,IntPtr.Zero,activated,0,0,0);
            healthy = keyboard != IntPtr.Zero && mouse != IntPtr.Zero && foreground!=IntPtr.Zero; Interlocked.Exchange(ref lastPump, Stopwatch.GetTimestamp()); ready.Set();
            while (Native.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                Interlocked.Exchange(ref lastPump, Stopwatch.GetTimestamp()); Native.TranslateMessage(ref message); Native.DispatchMessage(ref message);
            }
        }
        finally { healthy = false; ready.Set(); if (keyboard != IntPtr.Zero) Native.UnhookWindowsHookEx(keyboard); if (mouse != IntPtr.Zero) Native.UnhookWindowsHookEx(mouse);if(foreground!=IntPtr.Zero)Native.UnhookWinEvent(foreground); GC.KeepAlive(key); GC.KeepAlive(pointer);GC.KeepAlive(activated); }
    }
    public void Dispose() { if (threadId != 0) Native.PostThreadMessage(threadId, 0x12, UIntPtr.Zero, IntPtr.Zero); if (!thread.Join(2000)) throw new GuiException("ActivityMonitorStopUnconfirmed"); ready.Dispose(); }
}

public sealed class InteractionGuard : IDisposable
{
    private readonly int interactiveSession;
    private readonly IActivityMonitor monitor;
    private readonly TimeProvider time;
    private readonly Dictionary<string,(long Activity,long Foreground)> backgroundEpochs=new();
    public InteractionGuard(int interactiveSession,IActivityMonitor? monitor=null,TimeProvider? time=null) {this.interactiveSession=interactiveSession;this.monitor=monitor ?? new ActivityMonitor();this.time=time ?? TimeProvider.System;}
    private readonly Dictionary<string, (Semaphore Lease, long Sequence, DateTimeOffset Expires)> leases = new();
    public long ExternalInputEvents => monitor.Sequence;
    public long HardwareInputEvents => (monitor as ActivityMonitor)?.HardwareInputs ?? 0;
    public string State => monitor.Healthy ? "monitoring" : "unavailable";
    public void Arm(ControlSession session)
    {
        if (session.Info.Mode != "control") return;
        lock (leases)
        {
            if(session.Info.InteractionPolicy=="background-only") {if(!monitor.Healthy) throw new GuiException("ActivityMonitorUnavailable");if(session.Info.TargetScope.Any(p=>p.Pid==monitor.CurrentForegroundProcess))throw new GuiException("UserInputConflict"); backgroundEpochs[session.Info.ControlSessionId]=(monitor.Sequence,monitor.ForegroundSequence); return;}
            if (!monitor.Healthy || !monitor.Idle) throw new GuiException("UserInputConflict");
            var name = "Local\\DesktopTestDriver-input-" + WindowsIdentity.GetCurrent().User!.Value + "-" + interactiveSession;
            var lease = new Semaphore(1, 1, name);
            if (!lease.WaitOne(0)) { lease.Dispose(); throw new GuiException("DesktopLeaseBusy"); }
            if (leases.Remove(session.Info.ControlSessionId, out var old)) { old.Lease.Release(); old.Lease.Dispose(); }
            var expires = time.GetUtcNow().AddSeconds(session.Info.InteractionPolicy == "foreground-exclusive" ? 30 : 86400);
            leases[session.Info.ControlSessionId] = (lease, monitor.Sequence, expires);
            session.Info=session.Info with {LeaseExpiresAt=expires};
        }
    }
    public void Check(ControlSession session)
    {
        if (session.Info.Mode != "control") return;
        lock (leases)
        {
            if(session.Info.InteractionPolicy=="background-only")
            {
                if(!monitor.Healthy || !backgroundEpochs.TryGetValue(session.Info.ControlSessionId,out var epoch) || session.Info.TargetScope.Any(p=>monitor.LastActivityForProcess(p.Pid)>epoch.Activity || monitor.LastForegroundForProcess(p.Pid)>epoch.Foreground || p.Pid==monitor.CurrentForegroundProcess)) throw new GuiException("InputLeaseRevoked");
                return;
            }
            if (!monitor.Healthy || !leases.TryGetValue(session.Info.ControlSessionId, out var lease) || lease.Expires <= time.GetUtcNow() || lease.Sequence != monitor.Sequence) throw new GuiException("InputLeaseRevoked");
        }
    }
    public bool? ForegroundChangeObserved(ControlSession session)
    {
        lock(leases) return monitor.Healthy && backgroundEpochs.TryGetValue(session.Info.ControlSessionId,out var epoch)
            ? session.Info.TargetScope.Any(p=>monitor.LastForegroundForProcess(p.Pid)>epoch.Foreground || p.Pid==monitor.CurrentForegroundProcess) : null;
    }
    public void Release(string session) { lock (leases) { backgroundEpochs.Remove(session); if (leases.Remove(session, out var lease)) { lease.Lease.Release(); lease.Lease.Dispose(); } } }
    public void Ping() => monitor.Ping();
    public void Dispose() { lock (leases) { foreach (var key in leases.Keys.ToArray()) Release(key); } monitor.Dispose(); }
}

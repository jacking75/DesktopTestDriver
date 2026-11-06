using System.Runtime.InteropServices;

sealed class ForegroundProbe : IDisposable
{
    private delegate void Callback(IntPtr hook, uint evt, IntPtr window, int objectId, int childId, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)] private struct Message { public IntPtr Window; public uint Id; public UIntPtr WParam; public IntPtr LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, Callback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message,IntPtr window,uint min,uint max,uint remove);
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready = new();
    private uint threadId;
    private int count;
    private int foreignEvents;
    private bool installed;
    private int active;
    private int reported;
    private volatile bool stopping;
    public int Count => Volatile.Read(ref count);
    public int ForeignEvents => Volatile.Read(ref foreignEvents);
    public int ReportedEvents => Volatile.Read(ref reported);
    public ForegroundProbe(int pid)
    {
        thread = new Thread(() =>
        {
            threadId = GetCurrentThreadId();
            void Observe()
            {
                GetWindowThreadProcessId(GetForegroundWindow(),out var foregroundPid);
                var now=foregroundPid==pid ? 1 : 0;
                if(Interlocked.Exchange(ref active,now)==0 && now==1) Interlocked.Increment(ref count);
            }
            Callback callback = (_, _, window, _, _, _, _) =>
            {
                GetWindowThreadProcessId(window, out var actualPid);
                if (actualPid == pid) Interlocked.Increment(ref reported);
                else Interlocked.Increment(ref foreignEvents);
                Observe();
            };
            var hook = SetWinEventHook(3, 3, IntPtr.Zero, callback, (uint)pid, 0, 0);
            installed = hook != IntPtr.Zero;
            ready.Set();
            try { if (hook != IntPtr.Zero) while(!stopping) { while(PeekMessage(out _,IntPtr.Zero,0,0,1)) { } Observe(); Thread.Sleep(5); } }
            finally { if (hook != IntPtr.Zero) UnhookWinEvent(hook); GC.KeepAlive(callback); }
        }) { IsBackground = true };
        thread.Start(); if (!ready.Wait(1500) || !installed) throw new Exception("foreground monitor unavailable");
    }
    public void Dispose() { stopping=true; PostThreadMessage(threadId, 0x12, UIntPtr.Zero, IntPtr.Zero); if (!thread.Join(1500)) throw new Exception("foreground monitor stop unconfirmed"); ready.Dispose(); }
}

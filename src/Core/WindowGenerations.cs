using System.Collections.Concurrent;

namespace DesktopTestDriver;

// Window handles can be reused inside the same process. Creation/destruction events invalidate old IDs.
public sealed class WindowGenerations : IDisposable
{
    private readonly ConcurrentDictionary<long, long> versions = new();
    private readonly ManualResetEventSlim ready = new();
    private readonly Thread thread;
    private uint threadId;
    private bool installed;
    public WindowGenerations()
    {
        thread = new Thread(Pump) { IsBackground = true, Name = "DesktopTestDriver window lifetime" }; thread.Start();
        if (!ready.Wait(1500) || !installed) throw new GuiException("WindowTrackingUnavailable");
    }
    private void Pump()
    {
        threadId = Native.GetCurrentThreadId();
        Native.WinEventCallback callback = (_, _, window, objectId, childId, _, _) =>
        {
            if (window != IntPtr.Zero && objectId == 0 && childId == 0) versions.AddOrUpdate(window.ToInt64(), 1, (_, prior) => prior + 1);
        };
        var hook = Native.SetWinEventHook(0x8000, 0x8001, IntPtr.Zero, callback, 0, 0, 0);
        installed = hook != IntPtr.Zero; ready.Set();
        try { if (installed) while (Native.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0) { Native.TranslateMessage(ref message); Native.DispatchMessage(ref message); } }
        finally { if (hook != IntPtr.Zero) Native.UnhookWinEvent(hook); GC.KeepAlive(callback); }
    }
    public long Get(long window) => versions.GetValueOrDefault(window);
    public void Dispose() { Native.PostThreadMessage(threadId, 0x12, UIntPtr.Zero, IntPtr.Zero); thread.Join(1000); ready.Dispose(); }
}

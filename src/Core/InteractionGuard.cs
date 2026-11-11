using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DesktopTestDriver;

// Only counters and health are kept. Keys, pointer positions and text are never recorded.
public interface IActivityMonitor : IDisposable
{
    long Sequence { get; }
    bool Healthy { get; }
    long HardwareInputs => 0;
    void Ping();
}
public sealed class ActivityMonitor : IActivityMonitor
{
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready = new();
    private uint threadId;
    private volatile bool healthy;
    private long sequence, lastPump, hardwareInputs;
    public long HardwareInputs => Interlocked.Read(ref hardwareInputs);
    public long Sequence => Interlocked.Read(ref sequence);
    public bool Healthy => healthy && Stopwatch.GetElapsedTime(Interlocked.Read(ref lastPump)).TotalSeconds < 2;
    public ActivityMonitor()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "DesktopTestDriver activity monitor" };
        thread.Start(); if (!ready.Wait(2000)) throw new GuiException("ActivityMonitorUnavailable");
    }
    public void Ping() { if (threadId != 0) Native.PostThreadMessage(threadId, 0x8001, UIntPtr.Zero, IntPtr.Zero); }
    private void Run()
    {
        threadId = Native.GetCurrentThreadId(); _ = Native.PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        IntPtr keyboard = IntPtr.Zero, mouse = IntPtr.Zero;
        Native.HookCallback key = (code, message, data) =>
        {
            if (code >= 0) { var input = Marshal.PtrToStructure<Native.KeyHook>(data); if (input.Extra != WindowsInput.Marker) { Interlocked.Increment(ref sequence); if ((input.Flags & 0x10) == 0) Interlocked.Increment(ref hardwareInputs); } }
            return Native.CallNextHookEx(IntPtr.Zero, code, message, data);
        };
        Native.HookCallback pointer = (code, message, data) =>
        {
            if (code >= 0) { var input = Marshal.PtrToStructure<Native.MouseHook>(data); if (input.Extra != WindowsInput.Marker) { Interlocked.Increment(ref sequence); if ((input.Flags & 1) == 0) Interlocked.Increment(ref hardwareInputs); } }
            return Native.CallNextHookEx(IntPtr.Zero, code, message, data);
        };
        try
        {
            keyboard = Native.SetWindowsHookEx(13, key, IntPtr.Zero, 0); mouse = Native.SetWindowsHookEx(14, pointer, IntPtr.Zero, 0);
            healthy = keyboard != IntPtr.Zero && mouse != IntPtr.Zero; Interlocked.Exchange(ref lastPump, Stopwatch.GetTimestamp()); ready.Set();
            while (Native.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                Interlocked.Exchange(ref lastPump, Stopwatch.GetTimestamp()); Native.TranslateMessage(ref message); Native.DispatchMessage(ref message);
            }
        }
        finally { healthy = false; ready.Set(); if (keyboard != IntPtr.Zero) Native.UnhookWindowsHookEx(keyboard); if (mouse != IntPtr.Zero) Native.UnhookWindowsHookEx(mouse); GC.KeepAlive(key); GC.KeepAlive(pointer); }
    }
    public void Dispose() { if (threadId != 0) Native.PostThreadMessage(threadId, 0x12, UIntPtr.Zero, IntPtr.Zero); thread.Join(2000); ready.Dispose(); }
}

/// <summary>Input-session lease. Extended by every successful operation, capped at 15 minutes from arming.
/// Any human keyboard or mouse input revokes it immediately.</summary>
public sealed class InteractionGuard(IActivityMonitor? monitor = null, TimeProvider? time = null) : IDisposable
{
    public static readonly TimeSpan LeaseSlice = TimeSpan.FromSeconds(30), LeaseHardLimit = TimeSpan.FromSeconds(900);
    private readonly IActivityMonitor monitor = monitor ?? new ActivityMonitor();
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private readonly Dictionary<string, (long Sequence, DateTimeOffset Expires, DateTimeOffset HardLimit)> leases = new();
    public long ExternalInputEvents => monitor.Sequence;
    public long HardwareInputEvents => monitor.HardwareInputs;
    public string State => monitor.Healthy ? "monitoring" : "unavailable";
    public void Arm(ControlSession session)
    {
        if (!session.Info.Input) return;
        lock (leases)
        {
            if (!monitor.Healthy) throw new GuiException("ActivityMonitorUnavailable");
            var now = time.GetUtcNow(); var hard = now + LeaseHardLimit;
            leases[session.Info.ControlSessionId] = (monitor.Sequence, now + LeaseSlice, hard);
            session.Info = session.Info with { LeaseExpiresAt = now + LeaseSlice, LeaseHardLimitAt = hard };
        }
    }
    public void Check(ControlSession session)
    {
        if (!session.Info.Input) return;
        lock (leases)
        {
            if (!leases.TryGetValue(session.Info.ControlSessionId, out var lease)) throw new GuiException("InputLeaseRevoked", "NoLease") { NoEffect = true };
            if (!monitor.Healthy) throw new GuiException("InputLeaseRevoked", "ActivityMonitorUnavailable") { NoEffect = true };
            if (lease.Sequence != monitor.Sequence) throw new GuiException("InputLeaseRevoked", "HumanInputDetected") { NoEffect = true };
            var now = time.GetUtcNow();
            if (now >= lease.HardLimit) throw new GuiException("InputLeaseRevoked", "LeaseHardLimitReached") { NoEffect = true };
            if (now >= lease.Expires) throw new GuiException("InputLeaseRevoked", "LeaseExpired") { NoEffect = true };
        }
    }
    /// <summary>Pushes the lease to now + 30 seconds (never past the hard limit) after a successful operation.</summary>
    public void Extend(ControlSession session)
    {
        if (!session.Info.Input) return;
        lock (leases)
        {
            if (!leases.TryGetValue(session.Info.ControlSessionId, out var lease) || lease.Sequence != monitor.Sequence) return;
            var expires = time.GetUtcNow() + LeaseSlice; if (expires > lease.HardLimit) expires = lease.HardLimit;
            leases[session.Info.ControlSessionId] = lease with { Expires = expires };
            session.Info = session.Info with { LeaseExpiresAt = expires };
        }
    }
    public void Release(string session) { lock (leases) leases.Remove(session); }
    public void Ping() => monitor.Ping();
    public void Dispose() { lock (leases) leases.Clear(); monitor.Dispose(); }
}

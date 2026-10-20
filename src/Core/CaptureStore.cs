using System.Collections.Concurrent;
using System.Diagnostics;

namespace DesktopTestDriver;

public sealed record StoredCapture(CaptureInfo Info, DesktopWindow Window, long Tick, string File);
public sealed class CaptureStore(StateStore store, IGuiDesktop desktop, WindowRegistry windows)
{
    private readonly ConcurrentDictionary<string, StoredCapture> captures = new();
    public CaptureInfo Take(string windowId, CancellationToken cancellation)
    {
        var window = windows.Resolve(windowId); var bytes = desktop.Capture(window, cancellation);
        cancellation.ThrowIfCancellationRequested();
        var after = windows.Resolve(windowId);
        if (!WindowRegistry.Same(window, after) || after.Bounds != window.Bounds || after.Dpi != window.Dpi) throw new GuiException("StaleCapture");
        var id = "cap-" + Guid.NewGuid().ToString("N"); var name = id + ".png";
        using (var file = new FileStream(store.PathFor(name), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough)) { file.Write(bytes); file.Flush(true); }
        var now = DateTimeOffset.UtcNow;
        var info = new CaptureInfo(id, windowId, window.Bounds.Width, window.Bounds.Height, "window-client-pixels", now, now.AddSeconds(30), bytes.Length, GuiProtocol.Hash(bytes));
        var saved = new StoredCapture(info, window, Stopwatch.GetTimestamp(), name); store.Write(id + ".json", saved); captures[id] = saved;
        return info;
    }
    public PixelRect Validate(string windowId, string captureId)
    {
        if (!captures.TryGetValue(captureId, out var saved) || saved.Info.WindowId != windowId || Stopwatch.GetElapsedTime(saved.Tick).TotalSeconds > 30) throw new GuiException("StaleCapture");
        var current = windows.Resolve(windowId);
        if (!WindowRegistry.Same(saved.Window, current) || saved.Window.Bounds != current.Bounds || saved.Window.Dpi != current.Dpi) throw new GuiException("StaleCapture");
        return current.Bounds;
    }
    public byte[] Chunk(string id, long offset, int size)
    {
        if (!GuiProtocol.Id(id) || offset < 0 || size is < 1 or > 131072) throw new GuiException("InvalidCaptureChunk");
        var saved = captures.TryGetValue(id, out var c) ? c : store.Read<StoredCapture>(id + ".json");
        using var file = File.OpenRead(store.PathFor(saved.File));
        if (offset >= file.Length) throw new GuiException("InvalidCaptureChunk");
        file.Position = offset; var bytes = new byte[(int)Math.Min(size, file.Length - offset)]; file.ReadExactly(bytes); return bytes;
    }
}

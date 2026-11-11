using System.Collections.Concurrent;

namespace DesktopTestDriver;

public sealed record StoredCapture(CaptureInfo Info, DesktopWindow Window, DateTimeOffset At, string File)
{
    public bool Delivered { get; init; }
}
/// <summary>Agent-side capture copies. The PNG is deleted once the controller confirms receipt; undelivered copies
/// are deleted after 24 hours. Coordinates taken from a capture stay valid for 30 seconds.</summary>
public sealed class CaptureStore(StateStore store, IGuiDesktop desktop)
{
    public const long MaxBytes = 256L * 1024 * 1024;
    public static readonly TimeSpan CoordinateLifetime = TimeSpan.FromSeconds(30), Retention = TimeSpan.FromHours(24);
    private readonly ConcurrentDictionary<string, StoredCapture> captures = Load(store);
    private static ConcurrentDictionary<string, StoredCapture> Load(StateStore store)
    {
        var result = new ConcurrentDictionary<string, StoredCapture>();
        foreach (var file in Directory.GetFiles(store.Root, "cap-*.json"))
            try { var saved = store.Read<StoredCapture>(Path.GetFileName(file)); result[saved.Info.CaptureId] = saved; } catch { }
        return result;
    }
    public long Bytes => captures.Values.Where(c => !c.Delivered).Sum(c => c.Info.Bytes);
    public CaptureInfo Take(string windowId, DesktopWindow window, PixelRect? region, double scale, CancellationToken cancellation)
    {
        if (Bytes >= MaxBytes) throw new GuiException("EvidenceBudgetExceeded", "CaptureStorageFull") { NoEffect = true };
        var bytes = desktop.Capture(window, region, scale, cancellation);
        cancellation.ThrowIfCancellationRequested();
        var id = "cap-" + Guid.NewGuid().ToString("N")[..24]; var name = id + ".png";
        File.WriteAllBytes(store.PathFor(name), bytes);
        var now = DateTimeOffset.UtcNow; var area = region ?? new PixelRect(0, 0, window.Bounds.Width, window.Bounds.Height);
        var (width, height) = Png.Size(bytes);
        var info = new CaptureInfo(id, windowId, width, height, region == null && scale >= 1 ? "window-client-pixels" : "capture-pixels", now, now + CoordinateLifetime, bytes.Length, GuiProtocol.Hash(bytes))
        { Region = region, Scale = scale };
        var saved = new StoredCapture(info, window, now, name); store.Write(id + ".json", saved, durable: false); captures[id] = saved;
        return info;
    }
    /// <summary>Maps a point in capture pixels to window client pixels, checking the window has not moved or resized.</summary>
    public (int X, int Y) Map(string windowId, string captureId, int x, int y, DesktopWindow current)
    {
        if (!captures.TryGetValue(captureId, out var saved) || saved.Info.WindowId != windowId || DateTimeOffset.UtcNow - saved.At > CoordinateLifetime) throw new GuiException("StaleCapture") { NoEffect = true };
        if (!WindowQuery.Same(saved.Window, current) || saved.Window.Bounds != current.Bounds || saved.Window.Dpi != current.Dpi) throw new GuiException("StaleCapture", "WindowMovedOrResized") { NoEffect = true };
        if (x < 0 || y < 0 || x >= saved.Info.ImageWidth || y >= saved.Info.ImageHeight) throw new GuiException("InvalidPosition") { NoEffect = true };
        var region = saved.Info.Region ?? new PixelRect(0, 0, current.Bounds.Width, current.Bounds.Height);
        return (region.X + (int)Math.Round(x / saved.Info.Scale), region.Y + (int)Math.Round(y / saved.Info.Scale));
    }
    public byte[] Chunk(string id, long offset, int size)
    {
        if (!GuiProtocol.Id(id) || offset < 0 || size is < 1 or > 1_048_576) throw new GuiException("InvalidCaptureChunk");
        if (!captures.TryGetValue(id, out var saved) || saved.Delivered) throw new GuiException("CaptureNotFound");
        using var file = File.OpenRead(store.PathFor(saved.File));
        if (offset >= file.Length) throw new GuiException("InvalidCaptureChunk");
        file.Position = offset; var bytes = new byte[(int)Math.Min(size, file.Length - offset)]; file.ReadExactly(bytes); return bytes;
    }
    /// <summary>The controller saved and verified the image: drop the agent copy, keep the coordinate record.</summary>
    public void Acknowledge(string id)
    {
        if (!captures.TryGetValue(id, out var saved)) return;
        store.Delete(saved.File); var delivered = saved with { Delivered = true }; captures[id] = delivered; store.Write(id + ".json", delivered, durable: false);
    }
    public int Cleanup(ISet<string>? protectedCaptures = null)
    {
        var removed = 0;
        foreach (var c in captures.Values.ToArray())
        {
            var age = DateTimeOffset.UtcNow - c.At;
            var expired = c.Delivered ? age > CoordinateLifetime : age > Retention && protectedCaptures?.Contains(c.Info.CaptureId) != true;
            if (!expired) continue;
            store.Delete(c.File); store.Delete(c.Info.CaptureId + ".json"); captures.TryRemove(c.Info.CaptureId, out _); removed++;
        }
        return removed;
    }
}
internal static class Png
{
    public static (int Width, int Height) Size(byte[] png) =>
        png.Length >= 24 ? (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20))) : (0, 0);
}

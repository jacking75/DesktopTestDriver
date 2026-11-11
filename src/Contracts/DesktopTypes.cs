namespace DesktopTestDriver;

public sealed record PixelRect(int X, int Y, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= X && y >= Y && x < (long)X + Width && y < (long)Y + Height;
    public bool Intersects(PixelRect other) => X < (long)other.X + other.Width && other.X < (long)X + Width && Y < (long)other.Y + other.Height && other.Y < (long)Y + Height;
}
/// <summary>Process identity. PID reuse is told apart by the start time.</summary>
public sealed record DesktopProcess(int Pid, DateTimeOffset CreatedAt, string ImagePath);
public sealed record DesktopWindow(long Handle, DesktopProcess Process, string Title, PixelRect Bounds, uint Dpi, bool Foreground, long Generation = 0)
{
    public string ClassName { get; init; } = "";
    public long OwnerHandle { get; init; }
    public bool Minimized { get; init; }
    /// <summary>main, owned or popup.</summary>
    public string Kind { get; init; } = "main";
}
/// <param name="Source">Where the value came from: value, legacy.</param>
public sealed record ElementValue(bool Available, string? Text, string? Reason, string? Source = null);
public sealed record PublicWindow(string WindowId, string Title, int Pid, string ProcessName, PixelRect Bounds, bool Foreground)
{
    public string ClassName { get; init; } = "";
    public string Kind { get; init; } = "main";
    public string? OwnerWindowId { get; init; }
    public bool? Minimized { get; init; }
}
public sealed record CaptureInfo(string CaptureId, string WindowId, int ImageWidth, int ImageHeight, string CoordinateSpace,
    DateTimeOffset CapturedAt, DateTimeOffset ExpiresAt, long Bytes, string Sha256)
{
    public PixelRect? Region { get; init; }
    public double Scale { get; init; } = 1;
}

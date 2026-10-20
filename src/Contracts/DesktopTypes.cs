namespace DesktopTestDriver;

public sealed record PixelRect(int X, int Y, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= X && y >= Y && x < (long)X + Width && y < (long)Y + Height;
    public bool Intersects(PixelRect other) => X < (long)other.X + other.Width && other.X < (long)X + Width && Y < (long)other.Y + other.Height && other.Y < (long)Y + Height;
}
public sealed record DesktopProcess(int Pid, DateTimeOffset CreatedAt, string ImagePath, string ImageHash);
public sealed record DesktopWindow(long Handle, DesktopProcess Process, string Title, PixelRect Bounds, uint Dpi, bool Foreground, long Generation = 0);
public sealed record ElementValue(bool Available, string? Text, string? Reason);
public sealed record DesktopElement(string RuntimeId, string AutomationId, string Name, string ControlType, PixelRect Bounds, bool Enabled, ElementValue Value);
public sealed record PublicWindow(string WindowId, string Title, int Pid, DateTimeOffset CreatedAt, string ImagePath, PixelRect Bounds, bool Foreground);
public sealed record PublicElement(string ElementId, string AutomationId, string Name, string ControlType, PixelRect Bounds, bool Enabled, ElementValue Value);
public sealed record CaptureInfo(string CaptureId, string WindowId, int ImageWidth, int ImageHeight, string CoordinateSpace,
    DateTimeOffset CapturedAt, DateTimeOffset ExpiresAt, long Bytes, string Sha256);

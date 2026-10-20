using System.Runtime.InteropServices;
namespace DesktopTestDriver;

public sealed class WindowsInput : IInputBackend
{
    internal static readonly UIntPtr Marker = new(0x44544431);
    public bool IsDown(string kind, int code) => (Native.GetAsyncKeyState(kind == "button" ? code switch { 1 => 1, 2 => 2, 3 => 4, _ => throw new GuiException("InvalidRequest") } : code) & 0x8000) != 0;
    public void Down(string kind, int code) => Send(kind, code, false);
    public void Up(string kind, int code) => Send(kind, code, true);
    private static void Send(string kind, int code, bool up)
    {
        Native.Input input;
        if (kind == "button")
        {
            uint flag = code switch { 1 => up ? 4u : 2u, 2 => up ? 16u : 8u, 3 => up ? 64u : 32u, _ => throw new GuiException("InvalidRequest") };
            input = new() { Type = 0, Mouse = new() { Flags = flag, Extra = Marker } };
        }
        else input = new()
        {
            Type = 1,
            Keyboard = new()
            {
                Vk = kind == "unicode" ? (ushort)0 : (ushort)code,
                Scan = kind == "unicode" ? (ushort)code : (ushort)0,
                Flags = (up ? 2u : 0u) | (kind == "unicode" ? 4u : Extended(code)),
                Extra = Marker
            }
        };
        Inject(input);
    }
    private static uint Extended(int vk) => vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0xA3 or 0xA5 or 0x5B or 0x5C ? 1u : 0u;
    public void Move(int x, int y)
    {
        int left = Native.GetSystemMetrics(76), top = Native.GetSystemMetrics(77), width = Native.GetSystemMetrics(78), height = Native.GetSystemMetrics(79);
        if (width <= 1 || height <= 1 || x < left || y < top || x >= left + width || y >= top + height) throw new GuiException("InvalidRequest");
        Inject(new() { Type = 0, Mouse = new() { X = (int)Math.Round((x - left) * 65535.0 / (width - 1)), Y = (int)Math.Round((y - top) * 65535.0 / (height - 1)), Flags = 0xC001, Extra = Marker } });
    }
    public void Wheel(int delta) => Inject(new() { Type = 0, Mouse = new() { Data = unchecked((uint)delta), Flags = 0x800, Extra = Marker } });
    private static void Inject(Native.Input input)
    {
        if (Native.SendInput(1, [input], Marshal.SizeOf<Native.Input>()) != 1) throw new GuiException("InjectionFailed");
    }
}

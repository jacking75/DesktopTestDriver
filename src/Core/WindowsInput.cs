using System.Runtime.InteropServices;
namespace DesktopTestDriver;

public sealed class WindowsInput : IInputBackend
{
    internal static readonly UIntPtr Marker = new(0x44544431);
    public bool IsDown(string kind, int code) => kind != "unicode" && (Native.GetAsyncKeyState(kind == "button" ? code switch { 1 => 1, 2 => 2, 3 => 4, _ => throw new GuiException("InvalidRequest") } : code) & 0x8000) != 0;
    public void Send(IReadOnlyList<InputEvent> events)
    {
        if (events.Count == 0) return;
        var inputs = new Native.Input[events.Count];
        for (int i = 0; i < events.Count; i++) inputs[i] = Build(events[i]);
        if (Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.Input>()) != inputs.Length) throw new GuiException("InjectionFailed");
    }
    private static Native.Input Build(InputEvent e)
    {
        if (e.Kind == "button")
        {
            uint flag = e.Code switch { 1 => e.Up ? 4u : 2u, 2 => e.Up ? 16u : 8u, 3 => e.Up ? 64u : 32u, _ => throw new GuiException("InvalidRequest") };
            return new() { Type = 0, Mouse = new() { Flags = flag, Extra = Marker } };
        }
        var unicode = e.Kind == "unicode";
        return new()
        {
            Type = 1,
            Keyboard = new() { Vk = unicode ? (ushort)0 : (ushort)e.Code, Scan = unicode ? (ushort)e.Code : (ushort)0, Flags = (e.Up ? 2u : 0u) | (unicode ? 4u : Extended(e.Code)), Extra = Marker }
        };
    }
    private static uint Extended(int vk) => vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0xA3 or 0xA5 or 0x5B or 0x5C or 0x5D or 0x2C ? 1u : 0u;
    public void Move(int x, int y)
    {
        int left = Native.GetSystemMetrics(76), top = Native.GetSystemMetrics(77), width = Native.GetSystemMetrics(78), height = Native.GetSystemMetrics(79);
        if (width <= 1 || height <= 1 || x < left || y < top || x >= left + width || y >= top + height) throw new GuiException("InvalidPosition") { NoEffect = true };
        Inject(new() { Type = 0, Mouse = new() { X = (int)Math.Round((x - left) * 65535.0 / (width - 1)), Y = (int)Math.Round((y - top) * 65535.0 / (height - 1)), Flags = 0xC001, Extra = Marker } });
    }
    public void Wheel(int delta) => Inject(new() { Type = 0, Mouse = new() { Data = unchecked((uint)delta), Flags = 0x800, Extra = Marker } });
    private static void Inject(Native.Input input)
    {
        if (Native.SendInput(1, [input], Marshal.SizeOf<Native.Input>()) != 1) throw new GuiException("InjectionFailed");
    }
}

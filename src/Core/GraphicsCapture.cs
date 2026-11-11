using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DesktopTestDriver;

// Win32 capture item interop and free-threaded frame pools:
// https://learn.microsoft.com/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow
// https://learn.microsoft.com/uwp/api/windows.graphics.capture.direct3d11captureframepool.createfreethreaded
public static class GraphicsCapture
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IItemInterop { [PreserveSig] int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr item); }
    [DllImport("combase.dll")] private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, int length, out IntPtr value);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr name, ref Guid iid, out IntPtr factory);
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(IntPtr adapter, int type, IntPtr software, uint flags, IntPtr levels, uint count, uint sdk, out IntPtr device, out int level, out IntPtr context);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgi, out IntPtr device);
    private static GraphicsCaptureItem Item(IntPtr window)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out var hstring));
        IntPtr factory = IntPtr.Zero, item = IntPtr.Zero;
        try
        {
            var interopId = typeof(IItemInterop).GUID; Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, ref interopId, out factory));
            var interop = (IItemInterop)Marshal.GetObjectForIUnknown(factory);
            try
            {
                var itemId = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
                Marshal.ThrowExceptionForHR(interop.CreateForWindow(window, ref itemId, out item));
                return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(item);
            }
            finally { Marshal.ReleaseComObject(interop); }
        }
        finally { if (item != IntPtr.Zero) Marshal.Release(item); if (factory != IntPtr.Zero) Marshal.Release(factory); WindowsDeleteString(hstring); }
    }
    // The D3D device is created once per worker process and reused for every capture.
    private static IDirect3DDevice? device;
    private static IDirect3DDevice Device()
    {
        if (device != null) return device;
        IntPtr nativeDevice = IntPtr.Zero, context = IntPtr.Zero, dxgi = IntPtr.Zero, inspectable = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out nativeDevice, out _, out context));
            var dxgiId = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"); Marshal.ThrowExceptionForHR(Marshal.QueryInterface(nativeDevice, in dxgiId, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable));
            return device = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            if (inspectable != IntPtr.Zero) Marshal.Release(inspectable); if (dxgi != IntPtr.Zero) Marshal.Release(dxgi);
            if (context != IntPtr.Zero) Marshal.Release(context); if (nativeDevice != IntPtr.Zero) Marshal.Release(nativeDevice);
        }
    }
    /// <summary>Captures the client area (or a region of it) into a PNG. Runs inside the desktop worker.</summary>
    public static object Save(CaptureCommand c) => SaveAsync(c).GetAwaiter().GetResult();
    private static async Task<object> SaveAsync(CaptureCommand c)
    {
        if (!GraphicsCaptureSession.IsSupported()) throw new GuiException("ObservationUnavailable", "GraphicsCaptureUnsupported");
        var h = new IntPtr(c.Handle); Native.GetWindowThreadProcessId(h, out var actualPid);
        if (actualPid != c.Pid || Native.IsIconic(h)) throw new GuiException("WindowChanged");
        if (Native.DwmGetWindowRectAttribute(h, 9, out var rendered, Marshal.SizeOf<Native.NativeRect>()) != 0) throw new GuiException("ObservationUnavailable", "RenderedBoundsUnavailable");
        var client = c.Client; var region = c.Region ?? new PixelRect(0, 0, client.Width, client.Height);
        if (region.Width < 1 || region.Height < 1 || region.X + region.Width > client.Width || region.Y + region.Height > client.Height) throw new GuiException("InvalidRegion", "RegionOutsideClientArea");
        int cropX = client.X - rendered.Left + region.X, cropY = client.Y - rendered.Top + region.Y;
        var item = Item(h);
        if (cropX < 0 || cropY < 0 || cropX + region.Width > item.Size.Width || cropY + region.Height > item.Size.Height)
            throw new GuiException("ObservationUnavailable", "CaptureGeometryMismatch");
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(Device(), DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        using var session = pool.CreateCaptureSession(item); session.IsCursorCaptureEnabled = false;
        using var deadline = new CancellationTokenSource(1500); session.StartCapture();
        Direct3D11CaptureFrame? next = null;
        while (next == null) { deadline.Token.ThrowIfCancellationRequested(); next = pool.TryGetNextFrame(); if (next == null) await Task.Delay(5, deadline.Token); }
        using var frame = next;
        if (frame.ContentSize.Width != item.Size.Width || frame.ContentSize.Height != item.Size.Height) throw new GuiException("StaleCapture");
        using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface).AsTask(deadline.Token);
        using var converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var length = checked(converted.PixelWidth * converted.PixelHeight * 4);
        var buffer = new Windows.Storage.Streams.Buffer((uint)length); converted.CopyToBuffer(buffer);
        var bytes = new byte[length]; using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(bytes);
        var source = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, bytes, converted.PixelWidth * 4);
        BitmapSource cropped = new CroppedBitmap(source, new(cropX, cropY, region.Width, region.Height));
        if (c.Scale < 1) cropped = new TransformedBitmap(cropped, new ScaleTransform(c.Scale, c.Scale));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(cropped));
        using (var file = new FileStream(c.Destination, FileMode.CreateNew)) encoder.Save(file);
        return new { width = cropped.PixelWidth, height = cropped.PixelHeight };
    }
}

using System.Diagnostics;
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
    public static async Task Save(long handle, int pid, long startTicks, PixelRect client, string destination)
    {
        if (!GraphicsCaptureSession.IsSupported()) throw new GuiException("ObservationUnavailable", "GraphicsCaptureUnsupported");
        var h = new IntPtr(handle); Native.GetWindowThreadProcessId(h, out var actualPid);
        using var process = Process.GetProcessById(pid); using var current = Process.GetCurrentProcess();
        if (actualPid != pid || process.StartTime.ToUniversalTime().Ticks != startTicks || process.SessionId != current.SessionId || WindowsDesktop.Elevated(process.Handle) || Native.IsIconic(h)) throw new GuiException("WindowChanged");
        if (Native.DwmGetWindowRectAttribute(h, 9, out var rendered, Marshal.SizeOf<Native.NativeRect>()) != 0) throw new GuiException("ObservationUnavailable", "RenderedBoundsUnavailable");
        int cropX = client.X - rendered.Left, cropY = client.Y - rendered.Top;
        IntPtr nativeDevice = IntPtr.Zero, context = IntPtr.Zero, dxgi = IntPtr.Zero, inspectable = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out nativeDevice, out _, out context));
            var dxgiId = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"); Marshal.ThrowExceptionForHR(Marshal.QueryInterface(nativeDevice, in dxgiId, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable));
            using var device = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            var item = Item(h);
            if (item.Size.Width != rendered.Right - rendered.Left || item.Size.Height != rendered.Bottom - rendered.Top || cropX < 0 || cropY < 0 || cropX + client.Width > item.Size.Width || cropY + client.Height > item.Size.Height)
                throw new GuiException("ObservationUnavailable", "CaptureGeometryMismatch");
            using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
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
            var cropped = new CroppedBitmap(source, new(cropX, cropY, client.Width, client.Height));
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(cropped));
            using var file = new FileStream(destination, FileMode.CreateNew); encoder.Save(file); file.Flush(true);
        }
        finally
        {
            if (inspectable != IntPtr.Zero) Marshal.Release(inspectable); if (dxgi != IntPtr.Zero) Marshal.Release(dxgi);
            if (context != IntPtr.Zero) Marshal.Release(context); if (nativeDevice != IntPtr.Zero) Marshal.Release(nativeDevice);
        }
    }
}

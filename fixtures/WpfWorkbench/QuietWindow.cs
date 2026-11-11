using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
static class QuietWindow
{
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window,int index);
    [DllImport("user32.dll", EntryPoint="SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window,int index,IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    public static void Apply(Window window)
    {
        window.SourceInitialized+=(_,_)=> { var handle=new WindowInteropHelper(window).Handle; SetWindowLongPtr(handle,-20,new IntPtr(GetWindowLongPtr(handle,-20).ToInt64()|0x08000000)); };
        window.Loaded+=(_,_)=>SetWindowPos(new WindowInteropHelper(window).Handle,new IntPtr(1),0,0,0,0,0x13);
    }
}

using System.Runtime.InteropServices;
using System.Text;
namespace DesktopTestDriver;
public static class JournalProtection
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool CryptProtectData(ref Blob input,string description,ref Blob entropy,IntPtr reserved,IntPtr prompt,uint flags,out Blob output);
    [DllImport("crypt32.dll",SetLastError=true)] private static extern bool CryptUnprotectData(ref Blob input,IntPtr description,ref Blob entropy,IntPtr reserved,IntPtr prompt,uint flags,out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
    public static string Protect(string value,string purpose) => Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value),purpose,true));
    public static string Unprotect(string value,string purpose) => Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value),purpose,false));
    private static byte[] Transform(byte[] bytes,string purpose,bool protect)
    {
        var key=System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(purpose));
        var input=new Blob{Length=bytes.Length,Data=Marshal.AllocHGlobal(bytes.Length)}; var entropy=new Blob{Length=key.Length,Data=Marshal.AllocHGlobal(key.Length)}; Blob output=default;
        try
        {
            Marshal.Copy(bytes,0,input.Data,bytes.Length);Marshal.Copy(key,0,entropy.Data,key.Length);
            var ok=protect ? CryptProtectData(ref input,"DesktopTestDriver API 2 journal",ref entropy,IntPtr.Zero,IntPtr.Zero,1,out output) : CryptUnprotectData(ref input,IntPtr.Zero,ref entropy,IntPtr.Zero,IntPtr.Zero,1,out output);
            if(!ok) throw new GuiException("JournalProtectionFailed");
            var result=new byte[output.Length];Marshal.Copy(output.Data,result,0,result.Length);return result;
        }
        finally {Marshal.FreeHGlobal(input.Data);Marshal.FreeHGlobal(entropy.Data);if(output.Data!=IntPtr.Zero) LocalFree(output.Data);}
    }
}

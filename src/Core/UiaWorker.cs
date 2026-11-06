using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopTestDriver;

public sealed class UiaWorker : IDisposable
{
    private readonly Process helper;
    private readonly NamedPipeServerStream pipe;
    private readonly object gate = new();
    private bool disposed;
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
    public UiaWorker(string executable, CancellationToken cancellation)
    {
        var name = "dtd-uia-" + Guid.NewGuid().ToString("N");
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
        using var current = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false) };
        foreach (var arg in new[] { "--uia-worker", name, current.Id.ToString(), current.StartTime.ToUniversalTime().Ticks.ToString() }) start.ArgumentList.Add(arg);
        try
        {
            helper = Process.Start(start) ?? throw new GuiException("ProviderUnavailable");
            helper.StandardInput.WriteLine(token); helper.StandardInput.Close();
            // Drain both streams so a provider error cannot block helper termination.
            _ = helper.StandardOutput.ReadToEndAsync(); _ = helper.StandardError.ReadToEndAsync();
            pipe.WaitForConnectionAsync(cancellation).GetAwaiter().GetResult();
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid != helper.Id) throw new GuiException("WorkerIdentityMismatch");
            var hello = GuiWire.Read<string>(pipe, cancellation).GetAwaiter().GetResult();
            if (hello != token) throw new GuiException("WorkerIdentityMismatch");
        }
        catch { pipe.Dispose(); if (helper != null) { if (!helper.HasExited) { helper.Kill(); helper.WaitForExit(1000); } helper.Dispose(); } throw; }
    }
    public UiaResult Call(UiaCommand command, CancellationToken cancellation)
    {
        lock (gate)
        {
            if (disposed || helper.HasExited) throw new GuiException("ProviderUnavailable");
            try
            {
                GuiWire.Write(pipe, command, cancellation).GetAwaiter().GetResult();
                var response = GuiWire.Read<GuiResponse>(pipe, cancellation).GetAwaiter().GetResult();
                if (response.Status != "completed") throw new GuiException(response.Error?.Code ?? "ProviderUnavailable");
                return GuiProtocol.Decode<UiaResult>(GuiProtocol.Encode(response.Data));
            }
            catch { Dispose(); throw; }
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true; pipe.Dispose();
            try { if (!helper.HasExited) { helper.Kill(); if (!helper.WaitForExit(1000)) throw new GuiException("WorkerStopUnconfirmed"); } }
            finally { helper.Dispose(); }
        }
    }
    public static int Run(string name, int parentPid, long parentStart, string executable)
    {
        // This entire loop, including provider calls, stays on one dedicated MTA thread.
        int exitCode = 1;
        var thread = new Thread(() =>
        {
            try
            {
                var token = Console.ReadLine() ?? throw new GuiException("WorkerIdentityMismatch");
                using var parent = Process.GetProcessById(parentPid);
                if (parent.StartTime.ToUniversalTime().Ticks != parentStart || parent.MainModule?.FileName != executable) throw new GuiException("WorkerIdentityMismatch");
                using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
                using var startup = new CancellationTokenSource(3000); pipe.ConnectAsync(startup.Token).GetAwaiter().GetResult();
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid != parentPid) throw new GuiException("WorkerIdentityMismatch");
                GuiWire.Write(pipe, token, startup.Token).GetAwaiter().GetResult();
                while (!parent.HasExited)
                {
                    var command = GuiWire.Read<UiaCommand>(pipe, CancellationToken.None).GetAwaiter().GetResult();
                    GuiResponse response;
                    try { response = new("completed", Data: UiaAutomation.Execute(command)); }
                    catch (Exception e) { response = GuiResponse.Reject(e is GuiException g ? g.Code : "ProviderUnavailable"); }
                    GuiWire.Write(pipe, response, CancellationToken.None).GetAwaiter().GetResult();
                }
                exitCode = 0;
            }
            catch (EndOfStreamException) { exitCode = 0; }
            catch { exitCode = 1; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.MTA); thread.Start(); thread.Join(); return exitCode;
    }
}

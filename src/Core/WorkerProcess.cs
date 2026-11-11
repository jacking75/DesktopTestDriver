using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace DesktopTestDriver;

public sealed record CaptureCommand(long Handle, int Pid, long StartTicks, PixelRect Client, PixelRect? Region, double Scale, string Destination);
public sealed record FocusCommand(long Handle, int Pid);
/// <param name="Kind">uia, capture, focus</param>
public sealed record WorkerRequest(string Kind, UiaCommand? Uia = null, CaptureCommand? Capture = null, FocusCommand? Focus = null);

/// <summary>A long-lived helper process reached over a named pipe. UIA providers and graphics capture can hang;
/// a hung helper is killed and a new one started on the next call, while the agent itself stays responsive.</summary>
public sealed class WorkerProcess : IDisposable
{
    private readonly Process helper;
    private readonly NamedPipeServerStream pipe;
    private readonly object gate = new();
    private bool disposed;
    public bool Alive => !disposed && !helper.HasExited;
    public WorkerProcess(string executable)
    {
        var name = "dtd-worker-" + Guid.NewGuid().ToString("N");
        pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--worker"); start.ArgumentList.Add(name); start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            helper = Process.Start(start) ?? throw new GuiException("ProviderUnavailable");
            // Drain both streams so provider output can never block the helper.
            _ = helper.StandardOutput.ReadToEndAsync(); _ = helper.StandardError.ReadToEndAsync();
            using var startup = new CancellationTokenSource(5000);
            pipe.WaitForConnectionAsync(startup.Token).GetAwaiter().GetResult();
        }
        catch { pipe.Dispose(); if (helper != null) { try { if (!helper.HasExited) helper.Kill(); } catch { } helper.Dispose(); } throw new GuiException("ProviderUnavailable", "WorkerStartFailed"); }
    }
    /// <summary>Sends one request. Timeout or cancellation kills the helper: its state is unknown afterwards.</summary>
    public WorkerReply Call(WorkerRequest request, int timeoutMilliseconds, CancellationToken cancellation)
    {
        lock (gate)
        {
            if (!Alive) throw new GuiException("ProviderUnavailable");
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation); limit.CancelAfter(Math.Max(1, timeoutMilliseconds));
            try
            {
                GuiWire.Write(pipe, request, limit.Token).GetAwaiter().GetResult();
                return GuiWire.Read<WorkerReply>(pipe, limit.Token).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                Dispose();
                if (e is OperationCanceledException && cancellation.IsCancellationRequested) throw;
                throw new GuiException(e is OperationCanceledException ? "ProviderTimeout" : "ProviderUnavailable", e is OperationCanceledException ? "WorkerDidNotRespond" : e.GetType().Name);
            }
        }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        try { pipe.Dispose(); } catch { }
        try { if (!helper.HasExited) { helper.Kill(); helper.WaitForExit(1000); } } catch { }
        helper.Dispose();
    }
    /// <summary>Helper process entry point. One dedicated MTA thread serves every request so UIA objects stay on it.</summary>
    public static int Run(string name, int parentPid)
    {
        int exitCode = 1;
        var thread = new Thread(() =>
        {
            try
            {
                using var parent = Process.GetProcessById(parentPid);
                using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                using (var startup = new CancellationTokenSource(5000)) pipe.ConnectAsync(startup.Token).GetAwaiter().GetResult();
                while (!parent.HasExited)
                {
                    var request = GuiWire.Read<WorkerRequest>(pipe, CancellationToken.None).GetAwaiter().GetResult();
                    WorkerReply reply;
                    UiaAutomation.DispatchStarted = false;
                    try
                    {
                        object result = request.Kind switch
                        {
                            "uia" => UiaAutomation.Execute(request.Uia ?? throw new GuiException("InvalidRequest")),
                            "capture" => GraphicsCapture.Save(request.Capture ?? throw new GuiException("InvalidRequest")),
                            "focus" => WindowsDesktop.ActivateWindow(request.Focus!.Handle, request.Focus.Pid),
                            _ => throw new GuiException("InvalidRequest")
                        };
                        reply = new(true, JsonSerializer.SerializeToElement(result, GuiProtocol.Json), DispatchStarted: UiaAutomation.DispatchStarted);
                    }
                    catch (Exception e)
                    {
                        var error = e is GuiException g ? new GuiError(g.Code, g.Message, g.Details)
                            : e is System.Windows.Automation.ElementNotAvailableException ? new GuiError("StaleElement", "ElementNotAvailable")
                            : e is System.Windows.Automation.ElementNotEnabledException ? new GuiError("ElementDisabled", "ElementNotEnabled")
                            : new GuiError("ProviderError", e.GetType().Name + ": " + e.Message);
                        reply = new(false, Error: error, DispatchStarted: UiaAutomation.DispatchStarted);
                    }
                    byte[] frame;
                    try { frame = GuiWire.Frame(reply); }
                    catch (GuiException e) { frame = GuiWire.Frame(new WorkerReply(false, Error: new(e.Code, e.Message), DispatchStarted: reply.DispatchStarted)); }
                    pipe.Write(frame); pipe.Flush();
                }
                exitCode = 0;
            }
            catch (EndOfStreamException) { exitCode = 0; }
            catch (IOException) { exitCode = 0; }
            catch { exitCode = 1; }
            finally { UiaAutomation.DisposeWorker(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.MTA); thread.Start(); thread.Join(); return exitCode;
    }
}

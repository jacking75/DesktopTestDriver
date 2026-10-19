using System.Diagnostics;

namespace DesktopTestDriver;

public sealed class SshTransport(string python, string adapterPath, Machine machine, string runId) : IClientNode
{
    public string MachineId => machine.Id;
    public async Task<T> Invoke<T>(string operation, object? payload, CancellationToken cancellation)
    {
        if (!Wire.Id(runId) || operation is not ("install" or "install-agent" or "remove-agent" or "status" or "send" or "stop" or "remove" or "collect")) throw new DriverException(Error.InvalidRequest);
        var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(adapterPath);
        using var process = Process.Start(start) ?? throw new DriverException(Error.TransportLost);
        // The packet includes secret references, never credential values.
        await process.StandardInput.WriteLineAsync(Wire.Encode(new { operation, machine, runId, payload }));
        process.StandardInput.Close();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(operation is "install" or "install-agent" ? 120000 : 10000);
        var output = Lines.Read(process.StandardOutput, limit.Token);
        var errors = Lines.Drain(process.StandardError, limit.Token);
        try
        {
            await process.WaitForExitAsync(limit.Token);
            await Task.WhenAll(output, errors);
            if (process.ExitCode != 0 || output.Result == null) throw new DriverException(Error.TransportLost);
            return Wire.Decode<T>(output.Result);
        }
        catch { if (!process.HasExited) process.Kill(true); throw; }
    }
    public Task<Result> Execute(Command command, CancellationToken cancellation) => Invoke<Result>("send", command, cancellation);
    public async Task<bool> Stop(CancellationToken cancellation)
    {
        var result = await Invoke<CleanReceipt>("stop", null, cancellation);
        return result.Clean;
    }
    public sealed record CleanReceipt(bool Clean);
}

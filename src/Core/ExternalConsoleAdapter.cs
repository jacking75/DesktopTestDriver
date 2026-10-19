using System.Diagnostics;

namespace DesktopTestDriver;

public sealed record ConsoleAdapterConfig(string Executable, string ExecutableHash, string AdapterKind, int TimeoutMilliseconds);

// Vendor-specific tooling consumes fixed JSON probe/recover operations and returns a measured receipt.
// It is an explicitly configured executable, never a shell fragment supplied by a scenario.
public sealed class ExternalConsoleAdapter(ConsoleAdapterConfig config) : IRecoveryAdapter
{
    public string Kind => config.AdapterKind;
    public Task<RecoveryReceipt> Probe(string machineId, CancellationToken cancellation) => Invoke("probe", machineId, "probe", cancellation);
    public Task<RecoveryReceipt> Recover(string machineId, string runId, CancellationToken cancellation) => Invoke("recover", machineId, runId, cancellation);
    private async Task<RecoveryReceipt> Invoke(string operation, string machine, string run, CancellationToken cancellation)
    {
        if (!Wire.Id(machine) || !Wire.Id(run) || config.AdapterKind is not ("vm-console" or "hardware-kvm") || config.TimeoutMilliseconds is < 1 or > 5000 ||
            !Path.IsPathFullyQualified(config.Executable) || !File.Exists(config.Executable) || Wire.FileHash(config.Executable) != config.ExecutableHash)
            throw new DriverException(Error.RecoveryRequired);
        var start = new ProcessStartInfo(config.Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start) ?? throw new DriverException(Error.RecoveryRequired);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation); limit.CancelAfter(config.TimeoutMilliseconds);
        try
        {
            await process.StandardInput.WriteLineAsync(Wire.Encode(new { protocolVersion = Wire.Version, operation, machineId = machine, runId = run }));
            process.StandardInput.Close();
            var response = Lines.Read(process.StandardOutput, limit.Token);
            var errors = Lines.Drain(process.StandardError, limit.Token);
            await process.WaitForExitAsync(limit.Token);
            await errors;
            if (process.ExitCode != 0) throw new DriverException(Error.RecoveryRequired);
            var receipt = Wire.Decode<RecoveryReceipt>(await response ?? throw new DriverException(Error.RecoveryRequired));
            if (receipt.MachineId != machine || receipt.RunId != run || !RunConfig.IsHash(receipt.EvidenceHash)) throw new DriverException(Error.RecoveryRequired);
            return receipt;
        }
        catch { if (!process.HasExited) process.Kill(true); throw; }
    }
}

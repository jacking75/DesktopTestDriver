using System.Diagnostics;
using System.Text;

namespace DesktopTestDriver;

public sealed record FixtureBackendConfig(string Executable, string ExecutableHash, string RunId, Dictionary<string, string> DefinitionHashes,
    string[] Environments, RecoveryGrade Recovery, bool Simulated, int TimeoutMilliseconds = 3000);

// A concrete process transport for equipment/installer tooling, with the same fixed contract as the built-in backend.
// No shell fragments or scenario-provided executable/arguments cross this boundary.
public sealed class ExternalFixtureBackend(FixtureBackendConfig config) : IFixtureBackend
{
    public async Task<FixtureReceipt> Execute(FixtureDefinition fixture, FixtureStep step, string runId, CancellationToken cancellation)
    {
        fixture.Validate();
        var definitionHash = Wire.Hash(Encoding.UTF8.GetBytes(Wire.Encode(fixture)));
        if (!Wire.Id(runId) || config.RunId != runId || config.DefinitionHashes.GetValueOrDefault(fixture.Id) != definitionHash ||
            fixture.Environments.Except(config.Environments).Any() || config.Recovery < fixture.Recovery ||
            !fixture.Steps.Concat(fixture.Restore).Contains(step) || config.TimeoutMilliseconds is < 1 or > 5000 ||
            !Path.IsPathFullyQualified(config.Executable) || !RunConfig.IsHash(config.ExecutableHash)) throw new DriverException(Error.DelegationMismatch);
        for (FileSystemInfo? item = new FileInfo(config.Executable); item != null; item = item is FileInfo f ? f.Directory : ((DirectoryInfo)item).Parent)
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new DriverException(Error.DelegationMismatch);
        if (Wire.FileHash(config.Executable) != config.ExecutableHash) throw new DriverException(Error.DelegationMismatch);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(Math.Min(config.TimeoutMilliseconds, step.TimeoutMilliseconds));
        var start = new ProcessStartInfo(config.Executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start) ?? throw new DriverException(Error.ObservationUnavailable);
        try
        {
            var response = Lines.Read(process.StandardOutput, limit.Token);
            var errors = Lines.Drain(process.StandardError, limit.Token);
            await process.StandardInput.WriteLineAsync(Wire.Encode(new { protocolVersion = Wire.Version, operation = "execute", runId, fixtureId = fixture.Id,
                definitionHash, stepId = step.Id, fixtureOperation = step.Operation, expected = step.Expected, simulated = config.Simulated }).AsMemory(), limit.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(limit.Token);
            await errors;
            if (process.ExitCode != 0) throw new DriverException(Error.ObservationUnavailable);
            var receipt = Wire.Decode<FixtureReceipt>(await response ?? throw new DriverException(Error.ObservationUnavailable));
            if (receipt.RunId != runId || receipt.FixtureId != fixture.Id || receipt.StepId != step.Id ||
                receipt.Simulated != config.Simulated || !RunConfig.IsHash(receipt.EvidenceHash)) throw new DriverException(Error.ObservationUnavailable);
            return receipt;
        }
        catch { if (!process.HasExited) { process.Kill(true); process.WaitForExit(500); } throw; }
    }
}

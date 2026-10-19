using System.Diagnostics;

namespace DesktopTestDriver;

public interface IClientNode
{
    string MachineId { get; }
    Task<Result> Execute(Command command, CancellationToken cancellation);
    Task<bool> Stop(CancellationToken cancellation);
}
public sealed record BarrierEvidence(string MachineId, string Stage, bool Ready, string EvidenceHash);
public sealed class Campaign : IAsyncDisposable
{
    private readonly IClientNode[] nodes;
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly int maxMilliseconds;
    private readonly CancellationTokenSource stopped = new();
    private Task<bool>? stopTask;
    private readonly object sync = new();
    public bool IsStopped => stopped.IsCancellationRequested;
    public Campaign(IClientNode[] nodes, TimeSpan maximum)
    {
        if (nodes.Length == 0 || nodes.Select(n => n.MachineId).Distinct().Count() != nodes.Length || maximum.TotalMilliseconds is < 1 or > 900000)
            throw new DriverException(Error.InvalidRequest);
        this.nodes = nodes;
        maxMilliseconds = (int)maximum.TotalMilliseconds;
        stopped.CancelAfter(maxMilliseconds);
        _ = AutoStop();
    }
    private async Task AutoStop()
    {
        try { await Task.Delay(Timeout.Infinite, stopped.Token); }
        catch (OperationCanceledException) { await Stop(); }
    }
    public async Task<Result> Execute(string machine, Command command)
    {
        try
        {
            if (IsStopped || Stopwatch.GetElapsedTime(started).TotalMilliseconds >= maxMilliseconds) throw new DriverException(Error.Expired);
            var node = nodes.SingleOrDefault(n => n.MachineId == machine) ?? throw new DriverException(Error.InvalidRequest);
            var result = await node.Execute(command, stopped.Token).WaitAsync(stopped.Token);
            if (result.ErrorCode != Error.None) await Stop();
            return result;
        }
        catch { await Stop(); throw; }
    }
    public async Task Barrier(string stage, string[] required, Task<BarrierEvidence>[] evidence, TimeSpan timeout)
    {
        if (!Wire.Id(stage) || required.Length == 0 || required.Distinct().Count() != required.Length || required.Any(id => nodes.All(n => n.MachineId != id)) ||
            evidence.Length != required.Length || timeout.TotalMilliseconds is < 1 or > 5000) { await Stop(); throw new DriverException(Error.BarrierFailed); }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
            deadline.CancelAfter(timeout);
            var observed = await Task.WhenAll(evidence).WaitAsync(deadline.Token);
            if (observed.Select(e => e.MachineId).Distinct().Count() != required.Length || observed.Any(e => !e.Ready || e.Stage != stage || !required.Contains(e.MachineId) || !RunConfig.IsHash(e.EvidenceHash)))
                throw new DriverException(Error.BarrierFailed);
        }
        catch { await Stop(); throw new DriverException(Error.BarrierFailed); }
    }
    public Task<bool> Stop()
    {
        lock (sync) return stopTask ??= Task.Run(StopAll);
    }
    private async Task<bool> StopAll()
    {
        stopped.Cancel();
        var results = await Task.WhenAll(nodes.Select(async node =>
        {
            using var cleanup = new CancellationTokenSource(3000);
            try { return await node.Stop(cleanup.Token).WaitAsync(cleanup.Token); } catch { return false; }
        }));
        return results.All(r => r);
    }
    public async ValueTask DisposeAsync() { await Stop(); stopped.Dispose(); }
}

public interface IRecoveryAdapter
{
    string Kind { get; }
    Task<RecoveryReceipt> Probe(string machineId, CancellationToken cancellation);
    Task<RecoveryReceipt> Recover(string machineId, string runId, CancellationToken cancellation);
}
public sealed record RecoveryReceipt(string MachineId, string RunId, bool Independent, bool NormalDesktop, bool InputReleased, bool ResourcesClean, bool Simulated, string EvidenceHash);
public static class RecoveryGate
{
    public static void Require(RecoveryGrade needed, Machine machine, RecoveryReceipt receipt)
    {
        if (machine.Id != receipt.MachineId || receipt.Simulated || !RunConfig.IsHash(receipt.EvidenceHash) || machine.Recovery.Grade < needed ||
            !receipt.NormalDesktop || needed >= RecoveryGrade.R1 && (!receipt.InputReleased || !receipt.ResourcesClean) || needed == RecoveryGrade.R2 && !receipt.Independent)
            throw new DriverException(Error.RecoveryRequired);
        if (needed > RecoveryGrade.R0 && (machine.Recovery.EvidencePath == null || !File.Exists(machine.Recovery.EvidencePath) || Wire.FileHash(machine.Recovery.EvidencePath) != machine.Recovery.EvidenceHash))
            throw new DriverException(Error.RecoveryRequired);
    }
    public static RecoveryGrade RequiredFor(string scenario) => scenario switch
    {
        "inspect" => RecoveryGrade.R0,
        "desktop-input" => RecoveryGrade.R1,
        "uac" or "lock" or "sas" or "reboot" or "os-hang" => RecoveryGrade.R2,
        _ => throw new DriverException(Error.Unsupported)
    };
    public static async Task<RecoveryReceipt> Recover(IRecoveryAdapter adapter, string machine, string runId, TimeSpan maximum)
    {
        if (maximum.TotalMilliseconds is < 1 or > 5000) throw new DriverException(Error.InvalidRequest);
        using var cancellation = new CancellationTokenSource(maximum);
        return await adapter.Recover(machine, runId, cancellation.Token).WaitAsync(cancellation.Token);
    }
}
// A real VM/KVM vendor adapter must implement IRecoveryAdapter and supply independently measured evidence.
// No password entry, UAC suppression, SAS synthesis or automatic logon is provided here.
public sealed class UnsupportedRecoveryAdapter : IRecoveryAdapter
{
    public string Kind => "unavailable";
    public Task<RecoveryReceipt> Probe(string machineId, CancellationToken cancellation) => throw new DriverException(Error.RecoveryRequired);
    public Task<RecoveryReceipt> Recover(string machineId, string runId, CancellationToken cancellation) => throw new DriverException(Error.RecoveryRequired);
}

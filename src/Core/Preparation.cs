namespace DesktopTestDriver;

public sealed record PinnedFile(string Path, string Sha256);
public sealed record PreparedMachine(string MachineId, ClientStatus Status, Observation Observation, RecoveryReceipt Recovery, bool ClientLive = false, bool WatchdogLive = false);
public sealed record PreparationReport(bool ContractsReady, bool ReadyForRealExecution, bool Simulated, string[] Blockers);
public static class Preparation
{
    public static async Task<PreparedMachine> Observe(SshTransport node, NodeUi ui, RunConfig config, RecoveryReceipt recovery, CancellationToken cancellation)
    {
        // Broker status verifies both process identities on the target machine over host-key-pinned SSH.
        await using var lease = ui.BeginLease();
        var status = await node.Invoke<ClientStatus>("status",null,cancellation);
        var inspected = Wire.Decode<InspectResult>(Wire.Encode((await ui.Send("inspect",cancellation)).Evidence));
        return new(config.MachineId,status,inspected.Observation,recovery,true,true);
    }
    // Pure validation plus file reads. It cannot focus a window, launch a process or send input.
    public static PreparationReport Check(ScenarioPlan plan, ScenarioContext context, PinnedFile[] files, PreparedMachine[] machines, string evidenceRoot)
    {
        var blockers = new List<string>();
        try { context.Validate(plan); } catch (DriverException e) { blockers.Add(e.Code.ToString()); }
        try
        {
            RunStore.CheckPrivate(evidenceRoot);
            if (files.Length == 0 || files.Any(f => !RunConfig.IsHash(f.Sha256) || !File.Exists(f.Path) || Wire.FileHash(f.Path) != f.Sha256)) blockers.Add("CandidateFileHash");
            foreach (var config in context.Machines)
            {
                var observed = machines.SingleOrDefault(m => m.MachineId == config.MachineId);
                if (observed == null) { blockers.Add("MissingMachine"); continue; }
                if (observed.Status.Session != config.TargetSession || observed.Observation.Session != config.TargetSession || observed.Status.Desktop != "Default" ||
                    observed.Observation.Desktop != "Default" || observed.Observation.Elevated || observed.Observation.Monitors.Length == 0 ||
                    observed.Status.ManifestHash != Wire.Hash(System.Text.Encoding.UTF8.GetBytes(Wire.Encode(config)))) blockers.Add("SessionDesktopManifest");
                if (observed.Recovery.MachineId != config.MachineId || observed.Recovery.RunId != config.RunId ||
                    !observed.Recovery.NormalDesktop || !observed.Recovery.InputReleased || !observed.Recovery.ResourcesClean ||
                    plan.Recovery == RecoveryGrade.R2 && !observed.Recovery.Independent || !RunConfig.IsHash(observed.Recovery.EvidenceHash) ||
                    !context.Simulation && observed.Recovery.Simulated) blockers.Add("RecoveryUnproven");
                // Remote PIDs are validated by the pinned SSH Broker status operation, never against the Server's process table.
                if (!context.Simulation && (!observed.ClientLive || !observed.WatchdogLive)) blockers.Add("WatchdogNotAlive");
                if (config.Windows.Any(scope => !observed.Observation.Windows.Any(w => w.ImagePath.Equals(scope.ImagePath,StringComparison.OrdinalIgnoreCase) && w.ImageHash.Equals(scope.ImageHash,StringComparison.OrdinalIgnoreCase) && w.Title == scope.Title))) blockers.Add("WindowNotObserved");
            }
            if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(evidenceRoot))!).AvailableFreeSpace < context.Machines.Sum(m => m.Delegation!.Evidence.MaxBytes)) blockers.Add("EvidenceSpace");
        }
        catch (Exception e) when (e is not OutOfMemoryException) { blockers.Add("ReadOnlyPreparationFailed"); }
        if (context.Simulation) blockers.Add("D21-D23RealEnvironmentNotRun");
        var contracts = blockers.All(b => b == "D21-D23RealEnvironmentNotRun");
        return new(contracts, contracts && !context.Simulation, context.Simulation, blockers.Distinct().ToArray());
    }
}

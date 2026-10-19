using System.Text.RegularExpressions;

namespace DesktopTestDriver;

public sealed record SshSettings(string Host, int Port, string User, string HostKeyType, string HostKeyBase64, string SecretKind, string SecretReference);
public sealed record RecoverySettings(RecoveryGrade Grade, string Adapter, string? EvidencePath, string? EvidenceHash, DateTimeOffset? VerifiedAt);
public sealed record Machine(string Id, string Kind, string Owner, string Os, string Gpu, string[] Monitors, string[] Roles, string ConsoleUser,
    string InstallRoot, SshSettings Ssh, RecoverySettings Recovery);
public sealed record Inventory(int ProtocolVersion, Machine[] Machines)
{
    public void Validate()
    {
        if (ProtocolVersion != Wire.Version || Machines.Length is < 1 or > 64 || Machines.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Machines.Length)
            throw new DriverException(Error.InvalidRequest);
        foreach (var m in Machines)
        {
            if (!Wire.Id(m.Id) || !Enum.IsDefined(m.Recovery.Grade) || m.Kind is not ("physical" or "vm") || string.IsNullOrWhiteSpace(m.Owner) || m.Os is not ("windows10" or "windows11") ||
                m.Roles.Length == 0 || m.Roles.Any(r => !Wire.Id(r)) || m.Roles.Distinct().Count() != m.Roles.Length ||
                string.IsNullOrWhiteSpace(m.ConsoleUser) || !Path.IsPathFullyQualified(m.InstallRoot) || m.InstallRoot.Contains('"') ||
                !Regex.IsMatch(m.InstallRoot, @"^[A-Za-z]:[\\/][A-Za-z0-9 _./\\-]+$") || m.InstallRoot.Split(['\\', '/']).Contains("..") ||
                Path.GetPathRoot(m.InstallRoot) == m.InstallRoot || m.Ssh.Port is < 1 or > 65535 ||
                !Regex.IsMatch(m.Ssh.Host, @"^[A-Za-z0-9.:-]{1,253}$") || string.IsNullOrWhiteSpace(m.Ssh.User) ||
                m.Ssh.SecretKind is not ("password-env" or "keyfile-env") || !Regex.IsMatch(m.Ssh.SecretReference, "^[A-Za-z_][A-Za-z0-9_]{0,127}$") ||
                m.Ssh.HostKeyType is not ("ssh-ed25519" or "ecdsa-sha2-nistp256" or "ssh-rsa")) throw new DriverException(Error.InvalidRequest);
            try { if (Convert.FromBase64String(m.Ssh.HostKeyBase64).Length < 32) throw new FormatException(); }
            catch (FormatException) { throw new DriverException(Error.InvalidRequest); }
            if (m.Recovery.Grade > RecoveryGrade.R0 && (string.IsNullOrWhiteSpace(m.Recovery.EvidencePath) || m.Recovery.EvidenceHash == null || !RunConfig.IsHash(m.Recovery.EvidenceHash) ||
                m.Recovery.VerifiedAt == null || m.Recovery.VerifiedAt > DateTimeOffset.UtcNow || m.Recovery.Adapter == "mock")) throw new DriverException(Error.RecoveryRequired);
        }
    }
    public Machine Resolve(string id, string role)
    {
        Validate();
        var machine = Machines.SingleOrDefault(m => m.Id == id && m.Roles.Contains(role));
        return machine ?? throw new DriverException(Error.InvalidRequest);
    }
}
public sealed record Assignment(string MachineId, string Role);
public sealed record Reservation(string RunId, string MachineId, string[] Roles, string CandidateHash, string SettingsHash, DateTimeOffset StartsAt, DateTimeOffset ExpiresAt, string State);

public sealed class Reservations(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    private string FileFor(string machine)
    {
        if (!Wire.Id(machine)) throw new DriverException(Error.InvalidRequest);
        return Path.Combine(Root, machine.ToLowerInvariant() + ".json");
    }
    public void Acquire(Inventory inventory, string runId, Assignment[] assignments, string candidateHash, string settingsHash, DateTimeOffset expiry)
    {
        inventory.Validate();
        if (!Wire.Id(runId) || assignments.Length is < 1 or > 64 || assignments.Distinct().Count() != assignments.Length ||
            !RunConfig.IsHash(candidateHash) || !RunConfig.IsHash(settingsHash) || expiry <= DateTimeOffset.UtcNow || expiry > DateTimeOffset.UtcNow.AddMinutes(15)) throw new DriverException(Error.InvalidRequest);
        foreach (var a in assignments) inventory.Resolve(a.MachineId, a.Role);
        Directory.CreateDirectory(Root);
        var acquired = new List<string>();
        try
        {
            foreach (var group in assignments.GroupBy(a => a.MachineId).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var path = FileFor(group.Key);
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
                acquired.Add(path);
                var reservation = new Reservation(runId, group.Key, group.Select(a => a.Role).ToArray(), candidateHash, settingsHash, DateTimeOffset.UtcNow, expiry, "reserved");
                var bytes = System.Text.Encoding.UTF8.GetBytes(Wire.Encode(reservation));
                file.Write(bytes); file.Flush(true);
            }
        }
        catch (IOException)
        {
            foreach (var path in acquired) File.Delete(path);
            throw new DriverException(Error.ReservationConflict);
        }
    }
    public Reservation Read(string machine) => Wire.Decode<Reservation>(File.ReadAllText(FileFor(machine)));
    public void Complete(string machine, string runId, bool cleanupVerified)
    {
        _ = FileFor(machine);
        // Serializes read + delete, so another process cannot release a different owner's lease.
        using var locked = new FileStream(Path.Combine(Root, machine.ToLowerInvariant() + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var reservation = Read(machine);
        if (reservation.RunId != runId || !cleanupVerified) throw new DriverException(Error.CleanupIncomplete);
        File.Delete(FileFor(machine));
    }
}

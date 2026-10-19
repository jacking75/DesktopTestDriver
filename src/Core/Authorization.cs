using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace DesktopTestDriver;

// Trust is provisioned by the owner outside the deployment. A key arriving in run.json is never trusted.
public static class Authorization
{
    public static readonly string[] Actions = ["inspect", "snapshot", "focus", "move", "click", "wheel", "drag", "key-down", "key-up", "chord", "text", "wait", "heartbeat", "seal-input", "resume-input", "read-artifact", "fixture-start", "fixture-observe", "launch", "controls", "read-frame"];
    public static readonly string[] Permissions = ["screen", "control", "admin", "file", "chat", "clipboard", "diagnostics", "runbook"];
    public static byte[] Payload(RunConfig config) => Encoding.UTF8.GetBytes(Wire.Encode(config with
    { Delegation = config.Delegation! with { Signature = "" } }));
    public static RunConfig Sign(RunConfig config, ECDsa key) => config with
    { Delegation = config.Delegation! with { Signature = Convert.ToBase64String(key.SignData(Payload(config), HashAlgorithmName.SHA256)) } };
    // Development scope validation is not authentication: no owner key, signature or token is consulted.
    public static void ValidateDevelopment(RunConfig config)
    {
        var grant = config.Delegation ?? throw new DriverException(Error.InvalidRequest);
        if (grant.Actions.Length == 0 || grant.Actions.Except(Actions).Any() || !grant.Actions.Contains("heartbeat") ||
            grant.Evidence.RetainHours is < 1 or > 168 || grant.Evidence.MaxBytes is < 1024 or > 268_435_456)
            throw new DriverException(Error.InvalidRequest);
    }
    public static void Verify(RunConfig config, bool requireCurrentAccount = true)
    {
        var grant = config.Delegation ?? throw new DriverException(Error.DelegationMismatch);
        if (string.IsNullOrWhiteSpace(grant.Owner) || requireCurrentAccount && grant.Owner != WindowsIdentity.GetCurrent().Name || string.IsNullOrWhiteSpace(grant.Requester) || grant.Requester.Length > 80 ||
            !Wire.Id(grant.KeyId) || grant.Scenarios.Length is < 1 or > 128 || grant.Scenarios.Any(s => !Wire.Id(s)) ||
            grant.Actions.Length == 0 || grant.Actions.Except(Actions).Any() || grant.ProductPermissions.Except(Permissions).Any() ||
            !grant.ScenarioHashes.Keys.ToHashSet().SetEquals(grant.Scenarios) || grant.ScenarioHashes.Values.Any(h => !RunConfig.IsHash(h)) ||
            grant.Evidence.RetainHours is < 1 or > 168 || grant.Evidence.MaxBytes is < 1024 or > 268_435_456 ||
            config.AllowInput && !grant.Actions.Any(a => a is "click" or "text" or "key-down" or "move"))
            throw new DriverException(Error.DelegationMismatch);
        var trust = Environment.GetEnvironmentVariable("DTD_TRUST_STORE");
        if (trust == null || !Path.IsPathFullyQualified(trust)) throw new DriverException(Error.AuthenticationFailed);
        RunStore.CheckPrivate(Path.GetDirectoryName(trust)!);
        if ((File.GetAttributes(trust) & FileAttributes.ReparsePoint) != 0) throw new DriverException(Error.AuthenticationFailed);
        var owners = Wire.Decode<OwnerKey[]>(File.ReadAllText(trust));
        var matches = owners.Where(k => k.Owner == grant.Owner && k.KeyId == grant.KeyId).ToArray();
        if (matches.Length != 1) throw new DriverException(Error.AuthenticationFailed);
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(matches[0].PublicKey), out _);
            if (key.KeySize != 256 || !key.VerifyData(Payload(config), Convert.FromBase64String(grant.Signature), HashAlgorithmName.SHA256))
                throw new DriverException(Error.AuthenticationFailed);
        }
        catch (Exception e) when (e is CryptographicException or FormatException) { throw new DriverException(Error.AuthenticationFailed); }
    }
    public static void Require(RunConfig config, string action, string? scenario = null)
    {
        if (action is "stop" or "release-all") return;
        if (config.Delegation == null || !config.Delegation.Actions.Contains(action) || scenario != null && !config.Delegation.Scenarios.Contains(scenario))
            throw new DriverException(Error.DelegationMismatch);
        if (action == "snapshot" && !config.Delegation.Evidence.Images) throw new DriverException(Error.DelegationMismatch);
    }
}

public sealed record AuditEvent(string RunId, string MachineId, string ManifestHash, long Tick, DateTimeOffset At, string CommandHash, string Phase, Error Error);
public static class EvidenceVault
{
    public static void Audit(RunStore store, Command? command, string phase, Error error)
    {
        using var held = store.Lock();
        var record = new AuditEvent(store.Config.RunId, store.Config.MachineId, store.ManifestHash, System.Diagnostics.Stopwatch.GetTimestamp(),
            DateTimeOffset.UtcNow, command == null ? "" : Wire.Hash(Encoding.UTF8.GetBytes(Wire.Encode(command))), phase, error);
        var path = store.PathFor("audit.jsonl");
        if (File.Exists(path) && new FileInfo(path).Length > 4_194_304) throw new DriverException(Error.Expired);
        File.AppendAllText(path, Wire.Encode(record) + "\n", new UTF8Encoding(false));
    }
    public static void Budget(RunStore store)
    {
        var policy = store.Config.Delegation!.Evidence;
        long bytes = 0;
        foreach (var path in Directory.EnumerateFiles(store.Root))
        {
            // Atomic heartbeat/ledger replacement uses short-lived .tmp files. A concurrent rename or
            // wait frame removal is not lost observation and must not stop an otherwise healthy run.
            if (Path.GetExtension(path) == ".tmp") continue;
            try { bytes += new FileInfo(path).Length; }
            catch (FileNotFoundException) { continue; }
            if (bytes >= policy.MaxBytes) throw new DriverException(Error.Expired);
        }
    }
    // Remove evidence only. Operational ownership, secrets and the recovery ledger remain until explicit deployment removal.
    public static int PurgeExpired(RunStore store, DateTimeOffset now)
    {
        if (!store.Stopped || store.Read<InputLedger>("ledger.json") is not { Held.Length: 0, Processes.Length: 0 }) throw new DriverException(Error.CleanupIncomplete);
        if (now < store.Config.ExpiresAt.AddHours(store.Config.Delegation!.Evidence.RetainHours)) return 0;
        RunStore.CheckPrivate(store.Root);
        var files = Directory.GetFiles(store.Root).Where(p => Path.GetFileName(p).StartsWith("frame-", StringComparison.Ordinal) ||
            Path.GetFileName(p).StartsWith("command-", StringComparison.Ordinal) || Path.GetFileName(p) == "audit.jsonl").ToArray();
        foreach (var path in files) File.Delete(path);
        return files.Length;
    }
}

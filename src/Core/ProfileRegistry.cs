using System.Text;

namespace DesktopTestDriver;
public sealed record RegisteredProfile(ApplicationProfile Profile, MachineBindings Bindings, string ProfileHash, Dictionary<string,string> BinaryHashes);
public sealed record QualifiedAction(string Target, string Operation);
public sealed record BackgroundQualification(string ProfileId, string ProfileHash, Dictionary<string,string> BinaryHashes,
    string OsVersion, QualifiedAction[] Actions, DateTimeOffset QualifiedAt, string EvidencePath, string EvidenceHash,
    int GlobalInputEvents, int TargetForegroundEvents, bool Complete);

public sealed class ProfileRegistry(StateStore store)
{
    private static string Name(string id) => GuiProtocol.Id(id) ? "profile-" + id + ".json" : throw new GuiException("InvalidProfileId");
    public RegisteredProfile Read(string id) => store.Read<RegisteredProfile>(Name(id));
    public string[] List() => Directory.GetFiles(store.Root, "profile-*.json").Select(f => store.Read<RegisteredProfile>(Path.GetFileName(f)).Profile.ProfileId).ToArray();
    public RegisteredProfile Register(string profileJson, MachineBindings bindings)
    {
        if (ProfileValidator.Validate(profileJson).Any(d => d.State == "unsupported")) throw new GuiException("InvalidProfile");
        var profile = GuiProtocol.Decode<ApplicationProfile>(profileJson);
        if (bindings.Paths == null || bindings.Fingerprints == null || !bindings.Paths.TryGetValue(profile.Application.ExecutableBinding, out var exe) || !bindings.Paths.TryGetValue(profile.Application.WorkingDirectoryBinding, out var work) || !File.Exists(exe) || !Directory.Exists(work) || !Path.IsPathFullyQualified(exe) || !Path.IsPathFullyQualified(work)) throw new GuiException("InvalidBindings");
        var hashes = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in bindings.Paths.Values.Where(File.Exists)) hashes[Path.GetFullPath(path)] = GuiProtocol.Hash(File.ReadAllBytes(path));
        // Managed applications must also bind their entry assembly, not only the generic apphost.
        var assembly = Path.ChangeExtension(exe, ".dll"); if (File.Exists(assembly)) hashes[Path.GetFullPath(assembly)] = GuiProtocol.Hash(File.ReadAllBytes(assembly));
        if (!bindings.Fingerprints.TryGetValue(profile.Application.FingerprintBinding, out var expected) || !string.Equals(expected, hashes[Path.GetFullPath(exe)], StringComparison.OrdinalIgnoreCase)) throw new GuiException("ApplicationHashMismatch");
        var registered = new RegisteredProfile(profile, bindings, GuiProtocol.Hash(Encoding.UTF8.GetBytes(GuiProtocol.Encode(profile))), hashes);
        store.Write(Name(profile.ProfileId), registered); return registered;
    }
    public void Qualify(BackgroundQualification qualification)
    {
        var profile = Read(qualification.ProfileId);
        if (!qualification.Complete || qualification.GlobalInputEvents != 0 || qualification.TargetForegroundEvents != 0 || qualification.Actions.Length == 0 || qualification.ProfileHash != profile.ProfileHash || qualification.OsVersion != Environment.OSVersion.VersionString || qualification.QualifiedAt > DateTimeOffset.UtcNow || qualification.QualifiedAt < DateTimeOffset.UtcNow.AddDays(-30) || !File.Exists(qualification.EvidencePath) || GuiProtocol.Hash(File.ReadAllBytes(qualification.EvidencePath)) != qualification.EvidenceHash) throw new GuiException("InvalidQualification");
        CheckHashes(profile, qualification.BinaryHashes);
        foreach (var action in qualification.Actions)
            if (!profile.Profile.Targets.ContainsKey(action.Target) || !profile.Profile.Actions.Values.Any(a => a.Target == action.Target && a.Operation == action.Operation)) throw new GuiException("UnregisteredAction");
        store.Write("qualification-" + qualification.ProfileId + ".json", qualification);
    }
    private static void CheckHashes(RegisteredProfile registered, Dictionary<string,string> hashes)
    {
        if (hashes.Count != registered.BinaryHashes.Count || registered.BinaryHashes.Any(p => !hashes.TryGetValue(p.Key, out var hash) || hash != p.Value || !File.Exists(p.Key) || GuiProtocol.Hash(File.ReadAllBytes(p.Key)) != p.Value)) throw new GuiException("QualificationStale");
    }
    public RegisteredProfile ValidateProcess(string profileId, DesktopProcess process)
    {
        var registered = Read(profileId);
        var exe = Path.GetFullPath(registered.Bindings.Paths[registered.Profile.Application.ExecutableBinding]);
        if (!string.Equals(exe, process.ImagePath, StringComparison.OrdinalIgnoreCase) || registered.BinaryHashes[exe] != process.ImageHash) throw new GuiException("TargetScopeMismatch");
        CheckHashes(registered,registered.BinaryHashes);
        return registered;
    }
    public void Check(string? profileId, DesktopProcess process, string operation, ObservationNode node, Observation observation)
    {
        if (profileId == null) throw new GuiException("BackgroundQualificationRequired");
        var profile = ValidateProcess(profileId,process); var qualification = store.Read<BackgroundQualification>("qualification-" + profileId + ".json");
        if (qualification.ProfileHash != profile.ProfileHash || qualification.OsVersion != Environment.OSVersion.VersionString || qualification.QualifiedAt < DateTimeOffset.UtcNow.AddDays(-30) || !File.Exists(qualification.EvidencePath) || GuiProtocol.Hash(File.ReadAllBytes(qualification.EvidencePath)) != qualification.EvidenceHash) throw new GuiException("QualificationStale");
        CheckHashes(profile, qualification.BinaryHashes);
        var exe = Path.GetFullPath(profile.Bindings.Paths[profile.Profile.Application.ExecutableBinding]);
        if (!string.Equals(exe, process.ImagePath, StringComparison.OrdinalIgnoreCase) || profile.BinaryHashes[exe] != process.ImageHash) throw new GuiException("TargetScopeMismatch");
        if (!qualification.Actions.Any(a => a.Operation == operation && ElementQuery.Matches(node, profile.Profile.Targets[a.Target], observation))) throw new GuiException("ActionNotQualified");
    }
}

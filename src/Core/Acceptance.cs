using System.Text;

namespace DesktopTestDriver;

public sealed record AcceptanceCase(string Id, string Purpose, string[] PlanIds, string[] Environments, string[] Criteria, Verdict Status = Verdict.NotRun);
public static class AcceptanceGate
{
    public static void Validate(AcceptanceCase[] cases)
    {
        var required = Enumerable.Range(1,41).Select(i => $"V{i:00}").Concat(Enumerable.Range(1,12).Select(i => $"F{i:00}")).ToHashSet();
        if (cases.Length != 53 || !required.SetEquals(cases.Select(c => c.Id)) || cases.Any(c => c.PlanIds.Length == 0 || c.Criteria.Length == 0 || c.Environments.Length == 0 || c.Status != Verdict.NotRun))
            throw new DriverException(Error.InvalidRequest);
    }
    public static Verdict Evaluate(AcceptanceCase requirement, ScenarioReport[] reports, string candidateHash, string[] environments,
        IReadOnlyDictionary<string, bool> criterionEvidence, int distinctPhysicalMachines)
    {
        if (requirement.Environments.Except(environments).Any() || requirement.Id.StartsWith('F') && distinctPhysicalMachines < 2) return Verdict.Blocked;
        if (requirement.Criteria.Any(c => !criterionEvidence.TryGetValue(c,out var verified) || !verified)) return Verdict.NotRun;
        foreach (var id in requirement.PlanIds)
        {
            var matching = reports.Where(r => r.ScenarioId == id && r.CandidateHash == candidateHash && !r.Simulated).ToArray();
            if (matching.Length != 1) return Verdict.NotRun;
            if (matching[0].Verdict != Verdict.Pass || !matching[0].CleanupVerified) return Verdict.Fail;
        }
        return Verdict.Pass;
    }
}
public sealed record BundleFile(string Name, string Sha256, long Bytes);
public static class EvidenceBundle
{
    public static void Write(string directory, ScenarioPlan plan, ScenarioReport report)
    {
        if (plan.Id != report.ScenarioId || plan.Hash != report.PlanHash) throw new DriverException(Error.DelegationMismatch);
        RunStore.CreatePrivateDirectory(Path.GetFullPath(directory));
        Save(directory, plan, report);
    }
    // Bind the collected raw evidence too, and replace a provisional result with FAIL if collection did not finish.
    public static void Complete(string directory, ScenarioPlan plan, ScenarioReport report)
    {
        Verify(directory);
        if (plan.Id != report.ScenarioId || plan.Hash != report.PlanHash) throw new DriverException(Error.DelegationMismatch);
        Save(directory, plan, report);
    }
    private static void Save(string directory, ScenarioPlan plan, ScenarioReport report)
    {
        File.WriteAllText(Path.Combine(directory,"plan.json"), Wire.Encode(plan),new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory,"report.json"), Wire.Encode(report),new UTF8Encoding(false));
        // Publish only fixed metadata. Pairing, text, screenshots and adapter facts never enter this projection.
        File.WriteAllText(Path.Combine(directory,"public-summary.json"), Wire.Encode(new { report.RunId, report.ScenarioId, report.PlanHash, report.CandidateHash,
            report.ToolHash, report.SettingsHash, report.Verdict, report.Simulated, report.CleanupVerified,
            productAcceptance = "NOT RUN: requires per-criterion real environment evidence", steps = report.Steps.Select(s => new { s.Id, s.Verdict, s.Error }) }),new UTF8Encoding(false));
        var paths = new[] { "plan.json", "report.json", "public-summary.json" }.Select(n => Path.Combine(directory,n)).ToList();
        var raw = Path.Combine(directory,"client-evidence");
        if (Directory.Exists(raw)) { RunStore.CheckPrivate(raw); paths.AddRange(Directory.GetFiles(raw)); }
        var files = paths.Order().Select(p => new BundleFile(Path.GetRelativePath(directory,p).Replace('\\','/'),Wire.FileHash(p),new FileInfo(p).Length)).ToArray();
        File.WriteAllText(Path.Combine(directory,"bundle-manifest.json"),Wire.Encode(files),new UTF8Encoding(false));
    }
    public static void Verify(string directory)
    {
        RunStore.CheckPrivate(directory);
        var files = Wire.Decode<BundleFile[]>(File.ReadAllText(Path.Combine(directory,"bundle-manifest.json")));
        var required = new[] { "plan.json", "public-summary.json", "report.json" };
        if (required.Except(files.Select(f => f.Name)).Any() || files.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length ||
            files.Any(f => !required.Contains(f.Name) && (!f.Name.StartsWith("client-evidence/",StringComparison.Ordinal) ||
                f.Name[16..].Length == 0 || f.Name[16..].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')) || f.Name[16..] is "." or "..")))
            throw new DriverException(Error.InvalidRequest);
        foreach (var file in files)
        {
            var path = Path.Combine(directory,file.Name);
            if (file.Name.StartsWith("client-evidence/",StringComparison.Ordinal)) RunStore.CheckPrivate(Path.GetDirectoryName(path)!);
            if (Wire.FileHash(path) != file.Sha256 || new FileInfo(path).Length != file.Bytes) throw new DriverException(Error.DelegationMismatch);
        }
        var plan = Wire.Decode<ScenarioPlan>(File.ReadAllText(Path.Combine(directory,"plan.json"))); plan.Validate();
        var report = Wire.Decode<ScenarioReport>(File.ReadAllText(Path.Combine(directory,"report.json")));
        if (report.PlanHash != plan.Hash || report.ScenarioId != plan.Id) throw new DriverException(Error.DelegationMismatch);
    }
}

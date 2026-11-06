namespace DesktopTestDriver;
public sealed record EvidencePolicy(long MaxJournalBytes = 67_108_864, int FinalRequestRetentionHours = 24, long MaxCaptureBytes=268_435_456);
public static class EvidenceBudget
{
    public static void Admit(StateStore store, EvidencePolicy policy)
    {
        if (policy.MaxJournalBytes < 1_048_576 || policy.MaxCaptureBytes<1_048_576 || policy.FinalRequestRetentionHours is < 1 or > 8760) throw new GuiException("InvalidEvidencePolicy");
        AdmitCapture(store,policy,0);
        long bytes = 0;
        foreach (var path in Directory.GetFiles(store.Root, "request-*.json"))
        {
            var file = new FileInfo(path);
            var entry = RequestJournal.ReadEntry(store,file.Name);
            // An unknown effect and its original evidence survive retention cleanup.
            if (entry.Response.Status is "completed" or "rejected" or "failed" && entry.OriginalUnknownResponse == null && file.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-policy.FinalRequestRetentionHours))
            { File.Delete(path); continue; }
            bytes = checked(bytes + file.Length);
        }
        if (bytes >= policy.MaxJournalBytes) throw new GuiException("EvidenceBudgetExceeded");
    }
    public static void AdmitCapture(StateStore store,EvidencePolicy policy,long incomingBytes)
    {
        if(policy.MaxCaptureBytes<1_048_576 || incomingBytes<0)throw new GuiException("InvalidEvidencePolicy");
        var existing=Directory.GetFiles(store.Root,"cap-*.png").Sum(p=>new FileInfo(p).Length);
        if(existing+incomingBytes>policy.MaxCaptureBytes || incomingBytes==0 && existing>=policy.MaxCaptureBytes)throw new GuiException("EvidenceBudgetExceeded");
    }
}

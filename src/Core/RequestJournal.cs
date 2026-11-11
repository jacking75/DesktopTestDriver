using System.Collections.Concurrent;

namespace DesktopTestDriver;

public sealed record JournalEntry(string SessionId, string RequestId, string Fingerprint, string Operation,
    Deadline Deadline, GuiResponse Response, string? Output = null, string? ExpectedHash = null, long? ExpectedBytes = null,
    GuiResponse? AgentResponse = null, string? MachineId = null)
{
    public GuiResponse? OriginalUnknownResponse { get; init; }
    public string? Note { get; init; }
    public System.Text.Json.JsonElement? Arguments { get; init; }
}
public sealed record JournalIndexItem(string File, string Status, DateTimeOffset At, long Bytes, bool Unknown);
/// <summary>Durable record of mutations and captures. Reads are never written here.</summary>
public sealed class RequestJournal
{
    public const long MaxJournalBytes = 64L * 1024 * 1024;
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    private readonly StateStore store;
    private readonly ConcurrentDictionary<string, object> gates = new();
    private readonly ConcurrentDictionary<string, JournalIndexItem> index = new();
    private readonly ConcurrentDictionary<string, JournalEntry> recent = new();
    public RequestJournal(StateStore store)
    {
        this.store = store;
        var known = new Dictionary<string, JournalIndexItem>();
        try { if (store.Exists("journal-index.json")) foreach (var item in store.Read<JournalIndexItem[]>("journal-index.json")) known[item.File] = item; } catch { }
        foreach (var path in Directory.GetFiles(store.Root, "request-*.json"))
        {
            var file = new FileInfo(path);
            // Only records missing from the index, or still running, are opened at startup.
            if (known.TryGetValue(file.Name, out var listed) && listed.Status != "running" && listed.Bytes == file.Length) { index[file.Name] = listed; continue; }
            try { var entry = store.Read<JournalEntry>(file.Name); index[file.Name] = Item(file.Name, entry, file.Length, file.LastWriteTimeUtc); }
            catch { index[file.Name] = new(file.Name, "unreadable", file.LastWriteTimeUtc, file.Length, false); }
        }
        SaveIndex();
    }
    private static JournalIndexItem Item(string file, JournalEntry entry, long bytes, DateTimeOffset at) =>
        new(file, entry.Response.Status, at, bytes, entry.Response.Status is "running" or "completionUnknown" || entry.OriginalUnknownResponse != null);
    private static string Name(string session, string request) => "request-" + GuiProtocol.Hash(session + ":" + request)[..40] + ".json";
    public object Gate(string session, string request) => gates.GetOrAdd(Name(session, request), _ => new());
    public long Bytes => index.Values.Sum(i => i.Bytes);
    public int Count => index.Count;
    public JournalEntry? Find(string session, string request)
    {
        var name = Name(session, request);
        if (recent.TryGetValue(name, out var cached)) return cached;
        if (!index.ContainsKey(name) || !store.Exists(name)) return null;
        return store.Read<JournalEntry>(name);
    }
    public void Save(JournalEntry entry, bool durable = true)
    {
        var name = Name(entry.SessionId, entry.RequestId);
        var bytes = store.Write(name, entry, durable);
        recent[name] = entry; index[name] = Item(name, entry, bytes, DateTimeOffset.UtcNow);
        if (recent.Count > 256) foreach (var key in recent.Keys.Take(64).ToArray()) recent.TryRemove(key, out _);
    }
    /// <summary>Records the request before anything runs. Durable so a crash after dispatch is still visible.</summary>
    public (JournalEntry Entry, bool New) Accept(GuiRequest r, Deadline deadline, string? machine = null, string? output = null)
    {
        lock (Gate(r.ControlSessionId, r.RequestId))
        {
            var existing = Find(r.ControlSessionId, r.RequestId);
            if (existing != null)
            {
                if (existing.Fingerprint != GuiProtocol.Fingerprint(r)) throw new GuiException("DuplicateRequestMismatch");
                return (existing, false);
            }
            if (Bytes >= MaxJournalBytes) throw new GuiException("EvidenceBudgetExceeded");
            var entry = new JournalEntry(r.ControlSessionId, r.RequestId, GuiProtocol.Fingerprint(r), r.Operation, deadline,
                new("running", r.RequestId, r.ControlSessionId) { Operation = r.Operation, MachineId = machine }, output, MachineId: machine) { Note = r.Note, Arguments = r.Arguments };
            Save(entry); return (entry, true);
        }
    }
    /// <summary>The final record is not flushed: if it is lost the request reads as unknown, which errs on the safe side.</summary>
    public void Finish(GuiRequest r, GuiResponse response, GuiResponse? agentResponse = null)
    {
        lock (Gate(r.ControlSessionId, r.RequestId))
        {
            var entry = Find(r.ControlSessionId, r.RequestId) ?? throw new GuiException("RequestNotFound");
            if (entry.Response.Status is "running" or "completionUnknown")
                Save(entry with { Response = response, AgentResponse = agentResponse ?? entry.AgentResponse, OriginalUnknownResponse = entry.OriginalUnknownResponse ?? (response.Status == "completionUnknown" ? response : null) }, durable: false);
        }
    }
    public GuiResponse Result(string session, string request) => Find(session, request)?.Response ?? GuiResponse.Reject("RequestNotFound");
    public IEnumerable<JournalEntry> Entries(string? session = null)
    {
        foreach (var name in index.Keys.ToArray())
        {
            JournalEntry? entry = null;
            try { entry = recent.TryGetValue(name, out var c) ? c : store.Exists(name) ? store.Read<JournalEntry>(name) : null; } catch { }
            if (entry != null && (session == null || entry.SessionId == session)) yield return entry;
        }
    }
    public void Recover(Func<JournalEntry, GuiResponse>? resolve = null)
    {
        foreach (var item in index.Values.Where(i => i.Status == "running").ToArray())
        {
            var entry = store.Read<JournalEntry>(item.File);
            var response = resolve?.Invoke(entry) ?? entry.Response with { Status = "completionUnknown", Error = new("ExecutionInterrupted", "ExecutionInterrupted") };
            Save(entry with { Response = response, OriginalUnknownResponse = entry.OriginalUnknownResponse ?? (response.Status == "completionUnknown" ? response : null) });
        }
    }
    /// <summary>Deletes finished records older than the retention period. Uses the index only; no record is opened.</summary>
    public int Cleanup()
    {
        var removed = 0;
        foreach (var item in index.Values.ToArray())
        {
            if (item.Unknown || item.Status == "running" || DateTimeOffset.UtcNow - item.At < Retention) continue;
            store.Delete(item.File); index.TryRemove(item.File, out _); recent.TryRemove(item.File, out _); gates.TryRemove(item.File, out _); removed++;
        }
        SaveIndex(); return removed;
    }
    private void SaveIndex() { try { store.Write("journal-index.json", index.Values.OrderBy(i => i.At).ToArray(), durable: false); } catch (IOException) { } }
}

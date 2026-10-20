using System.Collections.Concurrent;

namespace DesktopTestDriver;

public sealed record JournalEntry(string SessionId, string RequestId, string Fingerprint, string Operation,
    Deadline Deadline, GuiResponse Response, string? Output = null, string? ExpectedHash = null, long? ExpectedBytes = null,
    GuiResponse? AgentResponse = null, string? MachineId = null, string? CertificateHash = null)
{
    public string Phase { get; init; } = "accepted";
}
public sealed class RequestJournal(StateStore store)
{
    private readonly ConcurrentDictionary<string, object> gates = new();
    private static string Name(string session, string request) => "request-" + GuiProtocol.Hash(System.Text.Encoding.UTF8.GetBytes(session + ":" + request)) + ".json";
    public object Gate(string session, string request) => gates.GetOrAdd(Name(session, request), _ => new());
    public JournalEntry? Find(string session, string request) => store.Exists(Name(session, request)) ? store.Read<JournalEntry>(Name(session, request)) : null;
    public void Save(JournalEntry entry) => store.Write(Name(entry.SessionId, entry.RequestId), entry);
    public (JournalEntry Entry, bool New) Accept(GuiRequest r, Deadline deadline, string? machine = null, string? certificate = null)
    {
        lock (Gate(r.ControlSessionId, r.RequestId))
        {
            var existing = Find(r.ControlSessionId, r.RequestId);
            if (existing != null)
            {
                if (existing.Fingerprint != GuiProtocol.Fingerprint(r)) throw new GuiException("DuplicateRequestMismatch");
                return (existing, false);
            }
            var entry = new JournalEntry(r.ControlSessionId, r.RequestId, GuiProtocol.Fingerprint(r), r.Operation, deadline,
                new("running", r.RequestId, r.ControlSessionId) { Operation = r.Operation, MachineId = machine }, r.Operation == "capture" ? GuiProtocol.Text(r.Arguments, "output") : null,
                MachineId: machine, CertificateHash: certificate);
            Save(entry); return (entry, true);
        }
    }
    public void Finish(GuiRequest r, GuiResponse response)
    {
        lock (Gate(r.ControlSessionId, r.RequestId))
        {
            var entry = Find(r.ControlSessionId, r.RequestId) ?? throw new GuiException("RequestNotFound");
            if (entry.Response.Status is "running" or "completionUnknown") Save(entry with { Response = response, Phase = "final" });
        }
    }
    public GuiResponse Result(string session, string request) => Find(session, request)?.Response ?? GuiResponse.Reject("RequestNotFound");
    public IEnumerable<JournalEntry> Entries() => Directory.GetFiles(store.Root, "request-*.json").Select(p => store.Read<JournalEntry>(Path.GetFileName(p)));
    public void Recover(bool controller)
    {
        foreach (var entry in Entries())
        {
            if (entry.Response.Status != "running") continue;
            var response = entry.Response with { Status = "completionUnknown", Error = new("ExecutionInterrupted", "ExecutionInterrupted") };
            if (controller && entry.AgentResponse is { Status: "completed" } agent)
            {
                if (entry.Operation != "capture") response = agent;
                else if (entry.Output != null && entry.ExpectedHash != null && File.Exists(entry.Output) && new FileInfo(entry.Output).Length == entry.ExpectedBytes && GuiProtocol.Hash(File.ReadAllBytes(entry.Output)) == entry.ExpectedHash)
                {
                    var data = System.Text.Json.JsonSerializer.SerializeToElement(agent.Data, GuiProtocol.Json);
                    var map = GuiProtocol.Decode<Dictionary<string, object?>>(data.GetRawText()); map["path"] = entry.Output;
                    response = agent with { Data = map };
                }
                else response = response with { Status = "failed", Error = new("ImageDeliveryInterrupted", "ImageDeliveryInterrupted") };
            }
            Save(entry with { Response = response });
        }
    }
}

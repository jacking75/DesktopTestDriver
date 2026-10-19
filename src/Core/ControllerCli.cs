using System.Text.Json;

namespace DesktopTestDriver;

public static class ControllerCli
{
    public static bool Successful(ControllerReply reply)
    {
        if (!reply.Ok) return false;
        if (reply.Data == null) return true;
        var data = JsonSerializer.SerializeToElement(reply.Data, Wire.Json);
        if (data.ValueKind != JsonValueKind.Object) return true;
        if (data.TryGetProperty("errorCode", out var error) && error.GetString() != "None") return false;
        if (data.TryGetProperty("clean", out var clean) && !clean.GetBoolean()) return false;
        if (data.TryGetProperty("normalDesktop", out var desktop) && !desktop.GetBoolean()) return false;
        if (data.TryGetProperty("verdict", out var verdict) && verdict.GetString() != "Pass") return false;
        return true;
    }
    public static async Task SaveFrame(string root, string machine, string frame, string output, CancellationToken cancellation)
    {
        if (!Guid.TryParseExact(frame, "N", out _)) throw new DriverException(Error.InvalidRequest);
        var destination = Path.GetFullPath(output);
        bool created = false;
        string? expectedHash = null;
        try
        {
            using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                long total = -1; string? hash = null;
                do
                {
                    var reply = await ControllerAgent.Call(root, new("request", machine, JsonSerializer.SerializeToElement(new { name = "read-frame", condition = frame, x = file.Length }, Wire.Json)), cancellation);
                    if (!Successful(reply)) throw new DriverException(Error.TransportLost);
                    var result = Wire.Decode<Result>(Wire.Encode(reply.Data));
                    var item = JsonSerializer.SerializeToElement(result.Evidence, Wire.Json);
                    var nextTotal = item.GetProperty("totalBytes").GetInt64();
                    var nextHash = item.GetProperty("sha256").GetString()!;
                    if (item.GetProperty("offset").GetInt64() != file.Length || item.GetProperty("frameId").GetString() != frame ||
                        nextTotal is < 1 or > 64_000_000 || !RunConfig.IsHash(nextHash) || total != -1 && (total != nextTotal || hash != nextHash))
                        throw new DriverException(Error.InvalidRequest);
                    total = nextTotal; hash = nextHash;
                    var bytes = Convert.FromBase64String(item.GetProperty("data").GetString()!);
                    if (bytes.Length is < 1 or > 65536 || file.Length + bytes.Length > total) throw new DriverException(Error.InvalidRequest);
                    await file.WriteAsync(bytes, cancellation);
                } while (file.Length < total);
                await file.FlushAsync(cancellation);
                // Hash after closing the exclusive writer below.
                expectedHash = hash;
            }
            if (Wire.FileHash(destination) != expectedHash) throw new DriverException(Error.InvalidRequest);
        }
        catch { if (created) File.Delete(destination); throw; }
    }
}

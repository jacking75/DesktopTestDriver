using System.Text;

namespace DesktopTestDriver;

public static class VisualMarker
{
    public static int[] Colors(string runId)
    {
        if (!Wire.Id(runId)) throw new DriverException(Error.InvalidRequest);
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(runId));
        int[] palette = [0x202020, 0xE0E0E0, 0xD03030, 0x30B0D0];
        return Enumerable.Range(0, 16).Select(i => palette[(hash[i / 4] >> (i % 4 * 2)) & 3]).ToArray();
    }
}

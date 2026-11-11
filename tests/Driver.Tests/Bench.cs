using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DesktopTestDriver;

/// <summary>Repeatable timings against the fake provider (0.1). Measures the driver's own overhead, not a real app.</summary>
internal static class Bench
{
    public static async Task<int> Run(string output)
    {
        var lines = new List<string> { $"# DesktopTestDriver {typeof(ControllerHost).Assembly.GetName().Version} bench {DateTimeOffset.Now:O}", $"# machine {Environment.MachineName}, {Environment.ProcessorCount} cpu, fake provider" };
        void Record(string name, int count, Stopwatch timer) { var line = $"{name,-34} n={count,5}  total={timer.ElapsedMilliseconds,6} ms  avg={timer.Elapsed.TotalMilliseconds / count,8:0.000} ms"; lines.Add(line); Console.WriteLine(line); }
        var c = new HandlerTests.Context(input: true); await c.OpenWindow();
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++) await c.Run("find-element", new { windowId = c.Window, selector = new { automationId = "MemoText" } });
        Record("handler find-element", 1000, timer);
        var (id, token) = await c.Find(new { automationId = "MemoText" });
        timer.Restart();
        for (int i = 0; i < 200; i++) await c.Run("set-value", new { windowId = c.Window, elementId = id, snapshotToken = token, value = "값 " + i });
        Record("handler set-value (journaled)", 200, timer);
        timer.Restart(); await c.Run("type-text", new { windowId = c.Window, text = new string('가', 500) }); Record("handler type-text 500 chars", 1, timer);
        timer.Restart();
        for (int i = 0; i < 20; i++) await c.Run("capture", new { windowId = c.Window });
        Record("handler capture", 20, timer);
        await using (var h = new NetworkTests.Hosts())
        {
            await h.Ready(); var (session, window) = await h.Open();
            timer.Restart();
            for (int i = 0; i < 20; i++) await h.Request(session, "find-element", new { windowId = window, selector = new { automationId = "MemoText" } });
            Record("pipe+tcp find-element (json-lines path)", 20, timer);
            var controller = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Controller", "Controller.exe"));
            if (File.Exists(controller))
            {
                var file = Path.Combine(h.Controller.StateDirectory, "bench-request.json");
                File.WriteAllText(file, GuiProtocol.Encode(new GuiRequest("bench", session, "find-element", 5000, JsonSerializer.SerializeToElement(new { windowId = window, selector = new { automationId = "MemoText" } }))), new UTF8Encoding(false));
                timer.Restart();
                for (int i = 0; i < 20; i++)
                {
                    var start = new ProcessStartInfo(controller) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                    foreach (var arg in new[] { "--state", h.Controller.StateDirectory, "--request", file }) start.ArgumentList.Add(arg);
                    using var p = Process.Start(start)!; await p.StandardOutput.ReadToEndAsync(); await p.WaitForExitAsync();
                }
                Record("CLI --request (process per call)", 20, timer);
            }
            else lines.Add("CLI --request: Controller.exe not found next to the tests");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllLines(output, lines);
        return 0;
    }
}

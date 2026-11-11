using DesktopTestDriver;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        WindowsDesktop.EnableDpi();
        var internalExit = InternalDesktopCommands.TryRun(args); if (internalExit.HasValue) return internalExit.Value;
        // In-process hosts log connection events to stderr; Windows PowerShell treats stderr from a native command as an error.
        if (Environment.GetEnvironmentVariable("DTD_TEST_VERBOSE") == null) Console.SetError(TextWriter.Null);
        return args switch
        {
            [] => Run(ContractTests.All.Concat(HandlerTests.All).Concat(NetworkTests.All)),
            ["--unit"] => Run(ContractTests.All.Concat(HandlerTests.All)),
            ["--network"] => Run(NetworkTests.All),
            ["--ui-tests"] => Run(UiTests.All),
            ["--ui-tests", var only] => Run(UiTests.All.Where(t => t.Name.Contains(only, StringComparison.OrdinalIgnoreCase))),
            ["--bench", var output] => Bench.Run(output).GetAwaiter().GetResult(),
            _ => 2
        };
    }
    static int Run(IEnumerable<(string Name, Func<Task> Body)> tests)
    {
        int passed = 0, failed = 0;
        foreach (var (name, body) in tests)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try { body().GetAwaiter().GetResult(); passed++; Console.WriteLine($"PASS {name} ({timer.ElapsedMilliseconds} ms)"); }
            catch (Exception e) { failed++; Console.WriteLine($"FAIL {name}: {e.GetType().Name}: {e.Message}"); }
        }
        Console.WriteLine($"{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }
}
internal static class T
{
    public static void Check(bool condition, string message = "assertion failed") { if (!condition) throw new Exception(message); }
    public static void Equal<TV>(TV expected, TV actual, string what = "") { if (!EqualityComparer<TV>.Default.Equals(expected, actual)) throw new Exception($"{what} expected <{expected}> but was <{actual}>"); }
    public static string Root(string kind = "local-tests") => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "artifacts", kind, Guid.NewGuid().ToString("N")));
    public static void Throws(string code, Action action)
    {
        try { action(); }
        catch (GuiException e) { Equal(code, e.Code, "error code"); return; }
        throw new Exception("expected " + code);
    }
    public static System.Text.Json.JsonElement Data(GuiResponse r) => GuiProtocol.Element(r.Data);
    public static GuiResponse Ok(GuiResponse r) { Check(r.Status == "completed", $"{r.Operation}: {r.Status} {r.Error?.Code} {r.Error?.Message}"); return r; }
}

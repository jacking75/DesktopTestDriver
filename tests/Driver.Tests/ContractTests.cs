using System.Text.Json;
using DesktopTestDriver;
using static T;

/// <summary>Request contract, argument validation, compact format and small building blocks.</summary>
internal static class ContractTests
{
    static GuiRequest Parse(object request) => GuiProtocol.Parse(JsonSerializer.Serialize(request));
    static void Reject(string code, object request) => Throws(code, () => Parse(request));
    static object Wait(object arguments) => new { requestId = "r1", controlSessionId = "s1", operation = "wait-condition", arguments };
    public static IEnumerable<(string, Func<Task>)> All =>
    [
        ("C01 one operation set without apiVersion; unknown fields and duplicates rejected", () =>
        {
            var r = Parse(new { requestId = "r1", controlSessionId = "s1", operation = "windows", arguments = new { } });
            Equal(5000, r.TimeoutMilliseconds, "default timeout");
            Reject("UnknownField", new { apiVersion = 2, requestId = "r1", controlSessionId = "s1", operation = "windows", arguments = new { } });
            Reject("UnsupportedOperation", new { requestId = "r1", controlSessionId = "s1", operation = "controls", arguments = new { windowId = "w1" } });
            Reject("UnknownField", new { requestId = "r1", controlSessionId = "s1", operation = "windows", arguments = new { extra = 1 } });
            Throws("DuplicateField", () => GuiProtocol.Parse("""{"requestId":"r","requestId":"r","controlSessionId":"s","operation":"windows","arguments":{}}"""));
            return Task.CompletedTask;
        }),
        ("C02 timeouts: waits up to 30 s, other operations 5 s", () =>
        {
            Parse(new { requestId = "r1", controlSessionId = "s1", operation = "wait-condition", timeoutMilliseconds = 30000, arguments = new { windowId = "w1", selector = new { automationId = "A" }, condition = "exists" } });
            Parse(new { requestId = "r1", controlSessionId = "s1", operation = "wait-window", timeoutMilliseconds = 30000, arguments = new { title = "x" } });
            Reject("InvalidTimeout", new { requestId = "r1", controlSessionId = "s1", operation = "wait-condition", timeoutMilliseconds = 30001, arguments = new { windowId = "w1", selector = new { automationId = "A" }, condition = "exists" } });
            Reject("InvalidTimeout", new { requestId = "r1", controlSessionId = "s1", operation = "invoke", timeoutMilliseconds = 5001, arguments = new { windowId = "w1", elementId = "e1", snapshotToken = "abcdefabcdef" } });
            return Task.CompletedTask;
        }),
        ("C03 3.6 wait conditions require a valid expected value", () =>
        {
            var s = new { automationId = "A" };
            Reject("InvalidCondition", Wait(new { windowId = "w1", selector = s, condition = "text-contains" }));
            Reject("InvalidCondition", Wait(new { windowId = "w1", selector = s, condition = "text-contains", expected = "" }));
            Reject("InvalidCondition", Wait(new { windowId = "w1", selector = s, condition = "value-equals" }));
            Reject("InvalidCondition", Wait(new { windowId = "w1", selector = s, condition = "toggle-equals", expected = "yes" }));
            Reject("InvalidCondition", Wait(new { windowId = "w1", selector = s, condition = "exists", expected = "x" }));
            Parse(Wait(new { windowId = "w1", selector = s, condition = "exists", expected = (string?)null }));
            Parse(Wait(new { windowId = "w1", selector = s, condition = "toggle-equals", expected = "indeterminate" }));
            Parse(Wait(new { windowId = "w1", selector = s, condition = "value-equals", expected = "" }));
            Parse(Wait(new { windowId = "w1", anyOf = new object[] { new { selector = s, condition = "exists" }, new { selector = new { name = "오류" }, condition = "exists" } } }));
            Reject("InvalidCondition", Wait(new { windowId = "w1", anyOf = new object[] { new { selector = s, condition = "exists" } }, selector = s }));
            return Task.CompletedTask;
        }),
        ("C04 8.2 selector operators: one name operator, bounded regex, index only at the top", () =>
        {
            Parse(new { requestId = "r1", controlSessionId = "s1", operation = "find-element", arguments = new { windowId = "w1", selector = new { nameRegex = "^저장.*$", index = 1 } } });
            Reject("InvalidSelector", new { requestId = "r1", controlSessionId = "s1", operation = "find-element", arguments = new { windowId = "w1", selector = new { name = "a", nameContains = "b" } } });
            Reject("InvalidSelector", new { requestId = "r1", controlSessionId = "s1", operation = "find-element", arguments = new { windowId = "w1", selector = new { nameRegex = new string('a', 257) } } });
            Reject("InvalidSelector", new { requestId = "r1", controlSessionId = "s1", operation = "find-element", arguments = new { windowId = "w1", selector = new { name = "a", parent = new { name = "b", index = 0 } } } });
            Reject("InvalidSelector", new { requestId = "r1", controlSessionId = "s1", operation = "find-element", arguments = new { windowId = "w1", selector = new { nameRegex = "(" } } });
            Reject("InvalidSelector", new { requestId = "r1", controlSessionId = "s1", operation = "find-element", arguments = new { windowId = "w1", selectors = Enumerable.Range(0, 17).Select(i => new { automationId = "a" + i }).ToArray() } });
            return Task.CompletedTask;
        }),
        ("C05 9.4 keys: punctuation, numpad, insert, apps; click count and modifiers", () =>
        {
            Equal(0xBB, GuiProtocol.Key("="), "="); Equal(0x60 + 7, GuiProtocol.Key("NUMPAD7"), "numpad"); Equal(45, GuiProtocol.Key("INSERT"), "insert"); Equal(0x5D, GuiProtocol.Key("APPS"), "apps");
            Throws("InvalidKey", () => GuiProtocol.Key("NUMPADX"));
            Parse(new { requestId = "r1", controlSessionId = "s1", operation = "chord", arguments = new { windowId = "w1", keys = new[] { "CTRL", "=" } } });
            Parse(new { requestId = "r1", controlSessionId = "s1", operation = "click", arguments = new { windowId = "w1", elementId = "e1", snapshotToken = "0123456789ab", count = 2, modifiers = new[] { "CTRL" } } });
            Reject("InvalidCount", new { requestId = "r1", controlSessionId = "s1", operation = "click", arguments = new { windowId = "w1", position = new { x = 1, y = 1 }, count = 4 } });
            Reject("AmbiguousTarget", new { requestId = "r1", controlSessionId = "s1", operation = "click", arguments = new { windowId = "w1", position = new { x = 1, y = 1 }, elementId = "e1", snapshotToken = "0123456789ab" } });
            return Task.CompletedTask;
        }),
        ("C06 4.3 Korean text is written as UTF-8, not \\u escapes", () =>
        {
            var json = GuiProtocol.Encode(new { name = "저장 완료" });
            Check(json.Contains("저장 완료") && !json.Contains("\\u"), json);
            return Task.CompletedTask;
        }),
        ("C07 fingerprint ignores field order and the note", () =>
        {
            var a = GuiProtocol.Parse("""{"requestId":"r","controlSessionId":"s","operation":"invoke","arguments":{"windowId":"w1","elementId":"e1","snapshotToken":"0123456789ab"},"note":"저장 누르기"}""");
            var b = GuiProtocol.Parse("""{"arguments":{"snapshotToken":"0123456789ab","elementId":"e1","windowId":"w1"},"operation":"invoke","controlSessionId":"s","requestId":"r"}""");
            Equal(GuiProtocol.Fingerprint(a), GuiProtocol.Fingerprint(b), "fingerprint");
            Reject("InvalidNote", new { requestId = "r1", controlSessionId = "s1", operation = "windows", arguments = new { }, note = new string('x', 121) });
            return Task.CompletedTask;
        }),
        ("C08 2.5 boot identifier falls back when the registry value is missing", () =>
        {
            Check(Deadline.BootId.Length > 0, "boot id");
            var fallback = Deadline.FallbackBootId(); Check(fallback.StartsWith("boot-"), fallback);
            Check(Deadline.Start(1000).Remaining > 0, "deadline usable");
            return Task.CompletedTask;
        }),
        ("C09 7.1 compact line format and short references", () =>
        {
            var refs = new ElementRefs(); var token = new string('a', 64);
            var e1 = refs.Export("w1", "control", "42-1", token); var again = refs.Export("w1", "control", "42-1", token);
            Equal("e1", e1, "first ref"); Equal(e1, again, "stable ref");
            Equal("42-1", refs.Resolve("e1", "aaaaaaaaaaaa", "w1").RuntimeId, "prefix token");
            Throws("StaleElement", () => refs.Resolve("e1", "aaaa", "w1"));
            Throws("StaleElement", () => refs.Resolve("e1", "bbbbbbbbbbbb", "w1"));
            Throws("TargetScopeMismatch", () => refs.Resolve("e1", "aaaaaaaaaaaa", "w2"));
            var nodes = new[]
            {
                new ObservationNode("e1", null, "", "간단 메모", "Window", "", "", true, false, new(false, null, null), ["Window"], "3fa9c1d20b7e"),
                new ObservationNode("e2", "e1", "MemoText", "메모", "Edit", "", "", true, false, new(true, "테스트 메모입니다", null), ["Value"], "91b04c77a2d1"),
                new ObservationNode("e4", "e1", "OpenButton", "열기", "Button", "", "", false, false, new(false, null, null), ["Invoke"], "0a7711e3bb52")
            };
            var text = CompactTree.Format(nodes, false);
            Equal("- Window \"간단 메모\" [e1 t=3fa9c1d20b7e]\n  - Edit #MemoText \"메모\" [e2 t=91b04c77a2d1] value=\"테스트 메모입니다\" patterns=Value\n  - Button #OpenButton \"열기\" [e4 t=0a7711e3bb52] disabled patterns=Invoke\n", text, "compact");
            var longValue = CompactTree.Line(nodes[1] with { Value = new(true, new string('x', 250), null) }, false);
            Check(longValue.Contains("…(+50)"), longValue);
            return Task.CompletedTask;
        }),
        ("C10 5.6 reference table is bounded", () =>
        {
            var refs = new ElementRefs();
            for (int i = 0; i < ElementRefs.Limit + 100; i++) refs.Export("w1", "control", "rid-" + i, "token-" + i.ToString("D8"));
            Equal(ElementRefs.Limit, refs.Count, "count");
            Throws("StaleElement", () => refs.Resolve("e1", "token-00000000", "w1"));
            return Task.CompletedTask;
        }),
        ("C11 4.1 frames: oversized values fail before any byte is written; 8 MiB limit", async () =>
        {
            using var stream = new MemoryStream();
            Throws("ResponseTooLarge", () => GuiWire.Frame(new string('x', GuiWire.MaxFrameBytes + 1)));
            Equal(0L, stream.Length, "nothing written");
            await GuiWire.Write(stream, new string('x', 3 * 1024 * 1024), CancellationToken.None);
            stream.Position = 0; Equal(3 * 1024 * 1024, (await GuiWire.Read<string>(stream, CancellationToken.None)).Length, "3 MiB frame");
        }),
        ("C12 1.1 settings come from arguments; default port 25001", () =>
        {
            var args = new List<string> { "--listen", "0.0.0.0:25010", "--state", @"C:\Temp\dtd-state", "--serve" };
            var c = HostSettings.Controller(args);
            Equal("0.0.0.0", c.ListenAddress, "address"); Equal(25010, c.Port, "port"); Equal(@"C:\Temp\dtd-state", c.StateDirectory, "state");
            Equal(1, args.Count, "options consumed");
            var a = HostSettings.Agent(["--controller", "192.168.0.10:25001", "--machine", "test-pc"]);
            Equal("192.168.0.10", a.ControllerHost, "host"); Equal("test-pc", a.MachineId, "machine");
            Equal(25001, new ControllerSettings("C:\\x").Port, "default port");
            Throws("InvalidConfiguration", () => HostSettings.Agent(["--controller", "nohostport"]));
            return Task.CompletedTask;
        }),
    ];
}

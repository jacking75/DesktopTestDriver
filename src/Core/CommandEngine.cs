using System.Diagnostics;
using System.Text;

namespace DesktopTestDriver;

public sealed class CommandEngine(RunStore store, IDesktop desktop, InputController input, Func<bool> watchdogAlive, IFixtureLauncher? fixtureLauncher = null) : IDisposable
{
    private readonly OwnedApplications applications = new();
    public void Dispose() => applications.Dispose();
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<string, (string Hash, Result Result)> results = [];
    private readonly Dictionary<string, Frame> frames = [];
    private readonly long started = Stopwatch.GetTimestamp();
    private long sequence;
    private long heartbeatSequence;
    private readonly object heartbeatGate = new();
    private readonly Dictionary<string, (string Hash, Result Result)> heartbeats = [];
    private static readonly HashSet<string> Names = ["inspect", "snapshot", "focus", "move", "click", "wheel", "drag", "key-down", "key-up", "chord", "text", "wait", "release-all", "stop", "heartbeat", "seal-input", "resume-input", "read-artifact", "fixture-start", "fixture-observe"];
    private static readonly HashSet<string> Inputs = ["move", "click", "wheel", "drag", "key-down", "key-up", "chord", "text"];

    public async Task<Result> Execute(Command command)
    {
        var began = Stopwatch.GetTimestamp();
        try { Bind(command); }
        catch (DriverException e) { TryAudit(command, "rejected", e.Code); return Answer(command, began, false, false, false, e.Code); }
        // Stop does not queue behind wait or a long sequence. Each input fragment checks the durable flag.
        if (command.Name == "stop")
        {
            store.Stop(Error.Stopped);
            var clean = input.Cleanup();
            TryAudit(command, "stopped", clean ? Error.None : Error.CleanupIncomplete);
            return Answer(command, began, true, true, clean, clean ? Error.None : Error.CleanupIncomplete, new { stopped = true, clean });
        }
        if (command.Name == "heartbeat")
        {
            lock (heartbeatGate)
            {
                var hash = Wire.Hash(Encoding.UTF8.GetBytes(Wire.Encode(command)));
                if (heartbeats.TryGetValue(command.CommandId, out var previous))
                    return previous.Hash == hash ? previous.Result : Answer(command, began, false, false, false, Error.DuplicateMismatch);
                try
                {
                    CheckLive(command);
                    if (command.Sequence <= heartbeatSequence || heartbeats.Count >= 4096) throw new DriverException(Error.SequenceRejected);
                    heartbeatSequence = command.Sequence;
                    store.Pulse("server-heartbeat");
                    EvidenceVault.Audit(store, command, "heartbeat", Error.None);
                    var result = Answer(command, began, true, true, true, Error.None, new { alive = true });
                    heartbeats.Add(command.CommandId, (hash, result));
                    return result;
                }
                catch (DriverException e) { return Answer(command, began, false, false, false, e.Code); }
            }
        }
        await gate.WaitAsync();
        bool accepted = false, executed = false;
        try
        {
            var hash = Wire.Hash(Encoding.UTF8.GetBytes(Wire.Encode(command)));
            if (results.TryGetValue(command.CommandId, out var old))
                return old.Hash == hash ? old.Result : Answer(command, began, false, false, false, Error.DuplicateMismatch);
            if (command.Sequence <= sequence) throw new DriverException(Error.SequenceRejected);
            if (results.Count >= 2048) throw new DriverException(Error.Expired);
            sequence = command.Sequence;
            accepted = true;
            CheckLive(command, allowStopped: command.Name == "release-all");
            EvidenceVault.Audit(store, command, "accepted", Error.None);
            // Persist acceptance before executing. A restarted Client is never allowed to re-run this directory.
            store.Write("command-" + command.CommandId + ".json", new { hash, phase = "accepted", sequence });
            object? evidence = null;
            bool verified = false;
            switch (command.Name)
            {
                case "launch": evidence = applications.Launch(store, command.ApplicationId ?? throw new DriverException(Error.InvalidRequest), () => CheckLive(command)); verified = true; break;
                case "inspect":
                    var observation = Inspect();
                    var windowFrames = observation.Windows.Select(window => Remember(observation, window, Stopwatch.GetTimestamp(), null, null, [])).ToArray();
                    evidence = new { observation, frames = windowFrames }; verified = true; break;
                case "snapshot": evidence = Snapshot(command, true); verified = true; break;
                case "controls": evidence = Snapshot(command, false, false, true); verified = true; break;
                case "read-frame": evidence = ReadFrame(command); verified = true; break;
                case "focus":
                    if (!store.Config.AllowInput || File.Exists(store.PathFor("input-sealed.json"))) throw new DriverException(Error.DelegationMismatch);
                    Guard(command, requireForeground: false);
                    if (!desktop.Focus(command.ExpectedWindow!)) throw new DriverException(Error.ForegroundMismatch);
                    evidence = Snapshot(command, false); verified = true; break;
                case "wait": evidence = await Wait(command); verified = true; break;
                case "read-artifact": evidence = ReadArtifact(command); verified = true; break;
                case "fixture-start":
                    evidence = await OwnedFixture.Start(store, desktop, fixtureLauncher ?? new FixtureLauncher(), () => CheckLive(command)); verified = true; break;
                case "fixture-observe": evidence = OwnedFixture.Observe(store, Inspect()); verified = true; break;
                case "seal-input":
                    if (command.Condition == null || !store.Config.Delegation!.Scenarios.Contains(command.Condition)) throw new DriverException(Error.DelegationMismatch);
                    if (!input.ReleaseAll()) throw new DriverException(Error.CleanupIncomplete);
                    store.Write("input-sealed.json", new { scenario = command.Condition }); verified = true; break;
                case "resume-input":
                    if (command.Condition == null || !store.Config.Delegation!.Scenarios.Contains(command.Condition)) throw new DriverException(Error.DelegationMismatch);
                    File.Delete(store.PathFor("input-sealed.json")); verified = true; break;
                case "release-all":
                    verified = input.ReleaseAll(); evidence = new { released = verified };
                    if (!verified) throw new DriverException(Error.CleanupIncomplete);
                    break;
                default:
                    if (!Inputs.Contains(command.Name)) throw new DriverException(Error.Unsupported);
                    if (!store.Config.AllowInput || File.Exists(store.PathFor("input-sealed.json"))) throw new DriverException(Error.DelegationMismatch);
                    Guard(command);
                    ValidateInput(command);
                    executed = true;
                    await Inject(command);
                    var after = Inspect();
                    // OS injection receipt is evidence only. The tested application's observer decides PASS.
                    evidence = new { source = "driver-local", injectionAccepted = true, after, applicationVerified = false };
                    break;
            }
            executed = true;
            var result = Answer(command, began, true, executed, verified, Error.None, evidence);
            results.Add(command.CommandId, (hash, result));
            store.Write("command-" + command.CommandId + ".json", new { hash, phase = "completed", result });
            EvidenceVault.Audit(store, command, "completed", Error.None);
            return result;
        }
        catch (Exception e)
        {
            var code = e is DriverException driver ? driver.Code : Error.ObservationUnavailable;
            if (accepted && command.Name != "heartbeat")
            {
                store.Stop(code);
                if (!input.Cleanup()) code = Error.CleanupIncomplete;
            }
            var result = Answer(command, began, accepted, executed, false, code);
            if (accepted)
            {
                var hash = Wire.Hash(Encoding.UTF8.GetBytes(Wire.Encode(command)));
                results[command.CommandId] = (hash, result);
                store.Write("command-" + command.CommandId + ".json", new { hash, phase = "failed", result });
                TryAudit(command, "failed", code);
            }
            return result;
        }
        finally { gate.Release(); }
    }
    private void TryAudit(Command command, string phase, Error code)
    {
        // Audit failure blocks normal acceptance, but cannot prevent emergency release/stop or hide its result.
        try { EvidenceVault.Audit(store,command,phase,code); } catch (Exception e) when (e is not OutOfMemoryException) { }
    }
    private static Result Answer(Command c, long start, bool accepted, bool executed, bool verified, Error error, object? evidence = null) =>
        new(c.CommandId, accepted, executed, verified, error, DateTimeOffset.UtcNow, Stopwatch.GetElapsedTime(start).TotalMilliseconds, evidence);
    private object ReadArtifact(Command command)
    {
        _ = Inspect();
        var scope = store.Config.ObservationFiles.SingleOrDefault(f => f.Id == command.Condition) ?? throw new DriverException(Error.DelegationMismatch);
        for (FileSystemInfo? file = new FileInfo(scope.Path); file != null; file = file is FileInfo f ? f.Directory : ((DirectoryInfo)file).Parent)
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0) throw new DriverException(Error.DelegationMismatch);
        using var stream = new FileStream(scope.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > scope.MaxBytes) throw new DriverException(Error.ObservationUnavailable);
        if (scope.Kind == "file") return new { id = scope.Id, hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)), size = stream.Length, source = "read-only-file-hash" };
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = reader.ReadToEnd();
        if (Encoding.UTF8.GetByteCount(content) > scope.MaxBytes) throw new DriverException(Error.ObservationUnavailable);
        var value = scope.Kind == "json" ? Wire.Decode<System.Text.Json.JsonElement>(content) :
            System.Text.Json.JsonSerializer.SerializeToElement(content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => Wire.Decode<System.Text.Json.JsonElement>(line)), Wire.Json);
        if (Encoding.UTF8.GetByteCount(Wire.Encode(value)) > Wire.MaxMessage / 2) throw new DriverException(Error.ObservationUnavailable);
        return new { id = scope.Id, hash = Wire.Hash(Encoding.UTF8.GetBytes(content)), value, source = "read-only-artifact" };
    }
    private void Bind(Command c)
    {
        var config = store.Config;
        if (c.ProtocolVersion != Wire.Version || !Wire.Id(c.CommandId) || c.Sequence <= 0 || !(Names.Contains(c.Name) || c.Name is "launch" or "controls" or "read-frame")) throw new DriverException(Error.InvalidRequest);
        if (c.RunId != config.RunId || c.MachineId != config.MachineId || c.Role != config.Role || c.ManifestHash != store.ManifestHash ||
            c.TargetSession != config.TargetSession || c.ExpectedDesktop != config.ExpectedDesktop) throw new DriverException(Error.DelegationMismatch);
        if (c.Deadline > config.ExpiresAt) throw new DriverException(Error.InvalidRequest);
        if (c.TimeoutMilliseconds is < 1 or > 5000) throw new DriverException(Error.InvalidRequest);
        Authorization.Require(config, c.Name);
        // Emergency release/stop remains available even after lease expiry.
        if (c.Name is not ("stop" or "release-all") && c.Deadline <= DateTimeOffset.UtcNow) throw new DriverException(Error.Expired);
    }
    private void CheckLive(Command c, bool allowStopped = false)
    {
        if (allowStopped) return;
        if (store.Stopped) throw new DriverException(Error.Stopped);
        if (c.Deadline <= DateTimeOffset.UtcNow || store.Config.ExpiresAt <= DateTimeOffset.UtcNow || Stopwatch.GetElapsedTime(started).TotalSeconds >= store.Config.MaxSeconds)
            throw new DriverException(Error.Expired);
        if (!watchdogAlive()) throw new DriverException(Error.WatchdogUnavailable);
        EvidenceVault.Budget(store);
        if (desktop.PhysicalConflict) throw new DriverException(Error.UserInputConflict);
    }
    private Observation Inspect()
    {
        var observation = desktop.Inspect();
        if (observation.Session != store.Config.TargetSession) throw new DriverException(Error.SessionChanged);
        if (observation.Desktop != store.Config.ExpectedDesktop || observation.Elevated) throw new DriverException(Error.DesktopChanged);
        return observation;
    }
    private bool Allowed(WindowIdentity w) => store.Config.Windows.Any(scope => (scope.Title == "*" || scope.Title == w.Title) &&
        string.Equals(scope.ImagePath, w.ImagePath, StringComparison.OrdinalIgnoreCase) && string.Equals(scope.ImageHash, w.ImageHash, StringComparison.OrdinalIgnoreCase));
    private object ReadFrame(Command command)
    {
        if (!store.Config.Delegation!.Evidence.Images || command.Condition == null || !frames.TryGetValue(command.Condition, out var frame) || frame.ImagePath == null || frame.ImageHash == null)
            throw new DriverException(Error.DelegationMismatch);
        var path = store.PathFor("frame-" + frame.Id + ".png");
        if (!path.Equals(frame.ImagePath, StringComparison.OrdinalIgnoreCase)) throw new DriverException(Error.DelegationMismatch);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (command.X < 0 || command.X > file.Length || file.Length > 64_000_000) throw new DriverException(Error.InvalidRequest);
        file.Position = command.X;
        var bytes = new byte[(int)Math.Min(65536, file.Length - command.X)];
        file.ReadExactly(bytes);
        return new { frameId = frame.Id, offset = command.X, totalBytes = file.Length, data = Convert.ToBase64String(bytes), sha256 = frame.ImageHash };
    }
    private bool SameWindow(WindowIdentity actual, WindowIdentity expected) => actual == expected ||
        actual with { Title = expected.Title } == expected && Allowed(actual) &&
        store.Config.Windows.Any(scope => scope.Title == "*" && scope.ImagePath.Equals(expected.ImagePath, StringComparison.OrdinalIgnoreCase) && scope.ImageHash == expected.ImageHash);
    private Frame Snapshot(Command c, bool image, bool requireForeground = true, bool readControls = false)
    {
        CheckLive(c);
        var before = Inspect();
        var window = c.ExpectedWindow ?? throw new DriverException(Error.InvalidRequest);
        if (!Allowed(window) || !before.Windows.Any(w => SameWindow(w, window))) throw new DriverException(Error.WindowChanged);
        if (requireForeground && before.Foreground != window.Hwnd) throw new DriverException(Error.ForegroundMismatch);
        var id = Guid.NewGuid().ToString("N");
        var captured = Stopwatch.GetTimestamp();
        string? path = null, hash = null;
        UiNode[] controls = [];
        if (image)
        {
            if (Directory.GetFiles(store.Root, "frame-*.png").Length >= 128) throw new DriverException(Error.Expired);
            path = store.PathFor("frame-" + id + ".png");
            hash = desktop.Capture(window, path);
            EvidenceVault.Budget(store);
            CheckLive(c);
            controls = desktop.Controls(window);
        }
        var after = Inspect();
        if (readControls) { controls = desktop.Controls(window); CheckLive(c); after = Inspect(); }
        if (!SameLayout(before, after) || !after.Windows.Any(w => SameWindow(w, window))) throw new DriverException(Error.WindowChanged);
        return Remember(after, after.Windows.Single(w => SameWindow(w, window)), captured, path, hash, controls, id);
    }
    private Frame Remember(Observation observation, WindowIdentity window, long captured, string? path, string? hash, UiNode[] controls, string? id = null)
    {
        var frame = new Frame(id ?? Guid.NewGuid().ToString("N"), captured, DateTimeOffset.UtcNow, observation, window, path, hash, controls);
        if (frames.Count >= 128) frames.Remove(frames.Keys.First());
        frames[frame.Id] = frame;
        return frame;
    }
    private static bool SameLayout(Observation a, Observation b) => a.Session == b.Session && a.Desktop == b.Desktop &&
        a.Foreground == b.Foreground && a.Monitors.SequenceEqual(b.Monitors);
    private Frame Guard(Command c, bool requireForeground = true)
    {
        CheckLive(c);
        if (c.FrameId == null || !frames.TryGetValue(c.FrameId, out var frame) ||
            Stopwatch.GetElapsedTime(frame.CapturedTick).TotalMilliseconds > store.Config.FrameMaxAgeMilliseconds) throw new DriverException(Error.StaleFrame);
        var window = c.ExpectedWindow ?? throw new DriverException(Error.InvalidRequest);
        if (frame.Window != window || !Allowed(window)) throw new DriverException(Error.WindowChanged);
        var current = Inspect();
        if (!current.Windows.Any(w => SameWindow(w, window))) throw new DriverException(Error.WindowChanged);
        if (current.Session != frame.Observation.Session || current.Desktop != frame.Observation.Desktop || !current.Monitors.SequenceEqual(frame.Observation.Monitors))
            throw new DriverException(Error.StaleFrame);
        if (requireForeground && (current.Foreground != window.Hwnd || frame.Observation.Foreground != window.Hwnd)) throw new DriverException(Error.ForegroundMismatch);
        return frame;
    }
    private void ValidateInput(Command c)
    {
        if (c.Keys.Length > 8 || c.Keys.Distinct().Count() != c.Keys.Length || c.Keys.Any(k => !store.Config.AllowedKeys.Contains(k))) throw new DriverException(Error.DelegationMismatch);
        if (c.Name is "key-down" or "key-up" && c.Keys.Length != 1 || c.Name == "chord" && c.Keys.Length == 0) throw new DriverException(Error.InvalidRequest);
        if (c.Name == "text" && (c.Text == null || c.Text.Length is < 1 or > 256 || c.Text.Any(ch => char.IsControl(ch)))) throw new DriverException(Error.InvalidRequest);
        if (c.Name is "move" or "click" or "drag" or "wheel")
        {
            var bounds = c.ExpectedWindow!.Bounds;
            if (c.X < 0 || c.Y < 0 || c.X >= bounds.Width || c.Y >= bounds.Height ||
                c.Name == "drag" && (c.EndX < 0 || c.EndY < 0 || c.EndX >= bounds.Width || c.EndY >= bounds.Height)) throw new DriverException(Error.InvalidRequest);
            if (c.Steps is < 1 or > 100 || Math.Abs((long)c.Delta) > 1200 || c.Button is not ("left" or "right" or "middle")) throw new DriverException(Error.InvalidRequest);
        }
    }
    private async Task Inject(Command c)
    {
        int Button() => c.Button switch { "left" => 1, "right" => 2, _ => 3 };
        void Move(int x, int y) { Guard(c); input.Move(checked(c.ExpectedWindow!.Bounds.X + x), checked(c.ExpectedWindow.Bounds.Y + y)); }
        void Down(string kind, int code) { Guard(c); input.Down(kind, code); }
        switch (c.Name)
        {
            case "move": Move(c.X, c.Y); break;
            case "click": Move(c.X, c.Y); Down("button", Button()); input.Up("button", Button()); break;
            case "wheel": Move(c.X, c.Y); Guard(c); input.Wheel(c.Delta); break;
            case "drag":
                Move(c.X, c.Y); Down("button", Button());
                try
                {
                    var end = Stopwatch.GetTimestamp();
                    for (int i = 1; i <= c.Steps; i++)
                    {
                        if (Stopwatch.GetElapsedTime(end).TotalMilliseconds >= store.Config.MaxHoldMilliseconds / 2.0) throw new DriverException(Error.Expired);
                        Move(c.X + (c.EndX - c.X) * i / c.Steps, c.Y + (c.EndY - c.Y) * i / c.Steps);
                        await Task.Yield();
                    }
                }
                finally { input.Up("button", Button()); }
                break;
            case "key-down": Down("key", c.Keys[0]); break;
            case "key-up": input.Up("key", c.Keys[0]); break;
            case "chord":
                try { foreach (var key in c.Keys) Down("key", key); }
                finally { foreach (var key in c.Keys.Reverse()) input.Up("key", key); }
                break;
            case "text":
                foreach (char ch in c.Text!)
                {
                    Down("unicode", ch);
                    input.Up("unicode", ch);
                    await Task.Yield();
                }
                break;
        }
    }
    private async Task<Frame> Wait(Command c)
    {
        if (c.Condition is not ("window" or "foreground" or "frame-change")) throw new DriverException(Error.InvalidRequest);
        var began = Stopwatch.GetTimestamp();
        string? oldHash = null;
        if (c.Condition == "frame-change")
        {
            if (c.FrameId == null || !frames.TryGetValue(c.FrameId, out var frame) || frame.ImageHash == null) throw new DriverException(Error.StaleFrame);
            oldHash = frame.ImageHash;
        }
        while (Stopwatch.GetElapsedTime(began).TotalMilliseconds < c.TimeoutMilliseconds)
        {
            CheckLive(c);
            var observation = Inspect();
            if (c.ExpectedWindow != null && observation.Windows.Contains(c.ExpectedWindow) && (c.Condition == "window" || observation.Foreground == c.ExpectedWindow.Hwnd))
            {
                var frame = Snapshot(c, c.Condition == "frame-change", c.Condition != "window");
                if (oldHash == null || frame.ImageHash != oldHash) return frame;
                if (frame.ImagePath != null) File.Delete(frame.ImagePath);
                frames.Remove(frame.Id);
            }
            await Task.Delay(20);
        }
        throw new DriverException(Error.Expired);
    }
}

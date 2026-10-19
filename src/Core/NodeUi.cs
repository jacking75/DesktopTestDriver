namespace DesktopTestDriver;

public sealed record UiSelector(string? AutomationId = null, string? Name = null, string? ControlType = null);
public sealed record InspectResult(Observation Observation, Frame[] Frames);
public interface IUiSurface
{
    Task<Frame> Read(string title, CancellationToken cancellation);
    Task Click(Frame frame, UiSelector selector, CancellationToken cancellation);
    Task Chord(Frame frame, int[] keys, CancellationToken cancellation);
}
// UIA supplies identity/geometry only. Clicks use the normal frame-guarded SendInput command path.
public sealed class NodeUi(IClientNode node, RunConfig config, string manifestHash) : IUiSurface
{
    private long sequence;
    public Task<bool> Stop(CancellationToken cancellation) => node.Stop(cancellation);
    public NodeLease BeginLease() => new(this, Math.Max(50, config.HeartbeatMilliseconds / 3));
    public async Task<Result> Send(string name, CancellationToken cancellation, Frame? frame = null, int x = 0, int y = 0, int[]? keys = null, string? condition = null,
        int endX = 0, int endY = 0, int delta = 0, string? text = null)
    {
        var result = await node.Execute(new Command { RunId = config.RunId, MachineId = config.MachineId, Role = config.Role, CommandId = Guid.NewGuid().ToString("N"),
            Sequence = Interlocked.Increment(ref sequence), TargetSession = config.TargetSession, ExpectedDesktop = config.ExpectedDesktop, ManifestHash = manifestHash,
            Deadline = new[] { config.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(5) }.Min(), Name = name, ExpectedWindow = frame?.Window, FrameId = frame?.Id,
            X = x, Y = y, Keys = keys ?? [], Condition = condition, EndX = endX, EndY = endY, Delta = delta, Text = text }, cancellation).WaitAsync(cancellation);
        if (result.ErrorCode != Error.None || !result.Accepted || !result.Executed) throw new DriverException(result.ErrorCode == Error.None ? Error.ObservationUnavailable : result.ErrorCode);
        return result;
    }
    public async Task<Frame> Read(string title, CancellationToken cancellation)
    {
        var result = await Send("inspect", cancellation);
        var inspection = Wire.Decode<InspectResult>(Wire.Encode(result.Evidence));
        var choices = inspection.Frames.Where(f => f.Window.Title == title).ToArray();
        if (choices.Length != 1) throw new DriverException(Error.WindowChanged);
        var meta = choices[0];
        if (inspection.Observation.Foreground != meta.Window.Hwnd)
            await Send("focus", cancellation, meta);
        return Wire.Decode<Frame>(Wire.Encode((await Send("snapshot", cancellation, meta)).Evidence));
    }
    public static UiNode Select(Frame frame, UiSelector selector)
    {
        if (selector.AutomationId == null && selector.Name == null) throw new DriverException(Error.InvalidRequest);
        var matches = frame.Controls.Where(c => (selector.AutomationId == null || c.AutomationId == selector.AutomationId) &&
            (selector.Name == null || c.Name == selector.Name) && (selector.ControlType == null || c.ControlType.EndsWith(selector.ControlType, StringComparison.Ordinal))).ToArray();
        if (matches.Length != 1 || !matches[0].Enabled || matches[0].Bounds.Width <= 0 || matches[0].Bounds.Height <= 0) throw new DriverException(Error.ObservationUnavailable);
        return matches[0];
    }
    public async Task Click(Frame frame, UiSelector selector, CancellationToken cancellation)
    {
        var control = Select(frame, selector); var r = control.Bounds;
        int x = r.X + r.Width / 2 - frame.Window.Bounds.X, y = r.Y + r.Height / 2 - frame.Window.Bounds.Y;
        if (!new Rect(0, 0, frame.Window.Bounds.Width, frame.Window.Bounds.Height).Contains(x, y)) throw new DriverException(Error.WindowChanged);
        await Send("click", cancellation, frame, x, y);
    }
    public async Task Chord(Frame frame, int[] keys, CancellationToken cancellation) => await Send("chord", cancellation, frame, keys: keys);
}
public sealed class NodeLease : IAsyncDisposable
{
    private readonly CancellationTokenSource stopped = new();
    private readonly Task pumping;
    private int broken;
    public bool Healthy => Volatile.Read(ref broken) == 0;
    public NodeLease(NodeUi node, int intervalMilliseconds) => pumping = Pump(node, intervalMilliseconds);
    private async Task Pump(NodeUi node, int intervalMilliseconds)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMilliseconds));
        try
        {
            do { await node.Send("heartbeat",stopped.Token); } while (await timer.WaitForNextTickAsync(stopped.Token));
        }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
        catch { Interlocked.Exchange(ref broken,1); }
    }
    public async ValueTask DisposeAsync() { stopped.Cancel(); await pumping; stopped.Dispose(); }
}

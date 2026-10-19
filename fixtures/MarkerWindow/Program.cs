using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using DesktopTestDriver;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is not [var run, var root, var duration] || !Wire.Id(run) || !int.TryParse(duration, out int seconds) || seconds is < 1 or > 300) return 2;
        try
        {
            RunStore.CheckPrivate(Path.GetFullPath(root)); WindowsDesktop.EnableDpi();
            var store = new RunStore(root);
            using var process = Process.GetCurrentProcess();
            var identity = Processes.Identify(process); var session = process.SessionId; var startedTick = Stopwatch.GetTimestamp();
            var scope = store.Config.Fixture ?? throw new DriverException(Error.DelegationMismatch);
            if (run != store.Config.RunId || seconds > scope.MaxSeconds || !scope.ImagePath.Equals(identity.ImagePath, StringComparison.OrdinalIgnoreCase) ||
                scope.ImageHash != identity.ImageHash || !WindowsDesktop.ConsoleValid(store.Config.TargetSession)) return 1;
            while (!File.Exists(store.PathFor("fixture.json")) && OwnedFixture.MayRun(store, identity, startedTick)) Thread.Sleep(10);
            if (!File.Exists(store.PathFor("fixture.json")) || !OwnedFixture.MayRun(store, identity, startedTick)) return 1;
            var colors = VisualMarker.Colors(run);
            var band = new System.Windows.Controls.Primitives.UniformGrid { Columns = 16, Rows = 1, Height = 64 };
            foreach (var rgb in colors) band.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)) });
            var stack = new StackPanel(); stack.Children.Add(band);
            stack.Children.Add(new TextBlock { Text = "DesktopTestDriver 전용 화면 표식 · " + run, Margin = new Thickness(12) });
            var click = new Button { Content = "독립 입력 시험", Height = 56, Margin = new Thickness(12) };
            var text = new TextBox { Height = 64, Margin = new Thickness(12), MaxLength = 4096, AcceptsReturn = true };
            System.Windows.Automation.AutomationProperties.SetAutomationId(click,"FixtureClick");
            System.Windows.Automation.AutomationProperties.SetAutomationId(text,"FixtureText");
            stack.Children.Add(click); stack.Children.Add(text);
            var window = new Window { Title = "DesktopTestDriver marker " + run, Width = 560, Height = 380, Content = stack };
            var events = new List<object>(); bool overflow = false; int clicks = 0, drag = 0, wheel = 0;
            void Record(string kind, int code, string action)
            {
                if (events.Count >= 4096) { overflow = true; return; }
                events.Add(new { tick = Stopwatch.GetTimestamp(), kind, code, action });
            }
            click.Click += (_,_) => clicks++;
            window.PreviewKeyDown += (_,e) => Record("key",KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key),"down");
            window.PreviewKeyUp += (_,e) => Record("key",KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key),"up");
            window.PreviewMouseDown += (_,e) => Record("button",(int)e.ChangedButton,"down");
            window.PreviewMouseUp += (_,e) => Record("button",(int)e.ChangedButton,"up");
            window.PreviewMouseMove += (_,e) => { if (e.LeftButton == MouseButtonState.Pressed) drag++; };
            window.PreviewMouseWheel += (_,_) => wheel++;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) }; var clock = Stopwatch.StartNew();
            var receipt = Path.Combine(root, "marker.json");
            timer.Tick += (_, _) =>
            {
                if (!OwnedFixture.MayRun(store, identity, startedTick) || clock.Elapsed.TotalSeconds >= seconds) { window.Close(); return; }
                var origin = band.PointToScreen(new(0, 0)); var end = band.PointToScreen(new(band.ActualWidth, band.ActualHeight));
                var json = Wire.Encode(new { runId = run, utc = DateTimeOffset.UtcNow, tick = Stopwatch.GetTimestamp(), identity, colors,
                    screenRegion = new DesktopTestDriver.Rect((int)origin.X, (int)origin.Y, (int)(end.X - origin.X), (int)(end.Y - origin.Y)) });
                File.WriteAllText(receipt + ".tmp", json); File.Move(receipt + ".tmp", receipt, true);
                var inputPath = Path.Combine(root,"fixture-input.json");
                var point = click.PointToScreen(new(click.ActualWidth/2,click.ActualHeight/2));
                var input = Wire.Encode(new { runId = run, utc = DateTimeOffset.UtcNow, tick = Stopwatch.GetTimestamp(), startTick = startedTick,
                    identity, session, window = new WindowInteropHelper(window).Handle.ToInt64(), foreground = window.IsActive,
                    clicks, drag, wheel, text = text.Text, events, observationValid = !overflow, observationOverflow = overflow,
                    heldVirtualKeys = Enumerable.Range(1,254).Where(vk => (GetAsyncKeyState(vk) & 0x8000) != 0).ToArray(), button = new { x = point.X, y = point.Y } });
                File.WriteAllText(inputPath + ".tmp",input); File.Move(inputPath + ".tmp",inputPath,true);
            };
            window.Loaded += (_, _) => timer.Start(); window.Closed += (_, _) => timer.Stop();
            new Application().Run(window); return 0;
        }
        catch { return 1; }
    }
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}

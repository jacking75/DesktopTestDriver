using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]) || File.Exists(args[0])) { MessageBox.Show("새 기록 파일의 절대 경로 하나가 필요하다."); return; }
        var output = args[0]; var rows = new List<object>(); var count = 0; bool complete = true; var began = DateTimeOffset.UtcNow;
        var app = new Application(); var window = new Window { Title = "입력 확인 창", Width = 660, Height = 560 }; var stack = new StackPanel(); var status = new TextBlock();
        var button = new Button { Content = "클릭 확인", Height = 50 }; AutomationProperties.SetAutomationId(button, "CounterButton");
        var edit = new TextBox { Height = 100, AcceptsReturn = true }; AutomationProperties.SetAutomationId(edit, "InputText");
        var drag = new Border { Height = 90, Background = System.Windows.Media.Brushes.LightSkyBlue, Child = new TextBlock { Text = "끌기 대상" } }; AutomationProperties.SetAutomationId(drag, "DragTarget");
        void Record(string kind, object? value = null)
        {
            if (rows.Count >= 10000) complete = false; else rows.Add(new { kind, value, at = DateTimeOffset.UtcNow });
            try { var tmp = output + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(new { pid = Environment.ProcessId, startedAt = began, observedAt = DateTimeOffset.UtcNow, complete, clicks = count, text = edit.Text, events = rows })); File.Move(tmp, output, true); } catch { complete = false; }
            status.Text = $"클릭 {count}회 / 기록 {rows.Count}개 / 기록 완전성 {complete}";
        }
        button.Click += (_, _) => { count++; Record("click"); }; edit.TextChanged += (_, _) => Record("text");
        window.PreviewKeyDown += (_, e) => Record("keyDown", e.Key.ToString()); window.PreviewKeyUp += (_, e) => Record("keyUp", e.Key.ToString());
        window.PreviewMouseDown += (_, e) => Record("mouseDown", new { button = e.ChangedButton.ToString(), point = e.GetPosition(stack).ToString() });
        window.PreviewMouseUp += (_, e) => Record("mouseUp", e.ChangedButton.ToString()); window.PreviewMouseWheel += (_, e) => Record("wheel", e.Delta);
        drag.MouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) Record("drag", e.GetPosition(drag).ToString()); };
        stack.Children.Add(status); stack.Children.Add(button); stack.Children.Add(edit); stack.Children.Add(drag);
        for (int i = 0; i < 30; i++) stack.Children.Add(new TextBlock { Text = "스크롤 항목 " + i, Height = 25 });
        window.Content = new ScrollViewer { Content = stack }; window.Loaded += (_, _) => Record("loaded"); window.Closed += (_, _) => Record("closed"); app.Run(window);
    }
}

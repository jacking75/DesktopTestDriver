using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

// WPF verification target: a combo box and a virtualized list (ItemContainer), a three-state check box,
// a slider (RangeValue), a menu whose drop-down is a separate popup window, and a delayed status change.
internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var quiet = args.Contains("--quiet");
        var app = new Application(); var window = new Window { Title = "WPF 검증 도구", Width = 520, Height = 640, ShowActivated = !quiet };
        if (quiet) QuietWindow.Apply(window);
        T Id<T>(T element, string id) where T : UIElement { AutomationProperties.SetAutomationId(element, id); return element; }
        var root = new DockPanel();
        var menu = Id(new Menu(), "MainMenu"); DockPanel.SetDock(menu, Dock.Top);
        var file = Id(new MenuItem { Header = "파일" }, "FileMenu");
        var status = Id(new TextBlock { Text = "준비", Margin = new Thickness(4) }, "Status");
        var create = Id(new MenuItem { Header = "새로 만들기" }, "NewItem"); create.Click += (_, _) => status.Text = "새로 만들기 선택";
        file.Items.Add(create); file.Items.Add(Id(new MenuItem { Header = "끝내기" }, "ExitItem")); menu.Items.Add(file); root.Children.Add(menu);
        var panel = new StackPanel { Margin = new Thickness(8) };
        var choice = Id(new ComboBox { ItemsSource = new[] { "첫째", "둘째", "셋째" }, SelectedIndex = 0, Width = 200, HorizontalAlignment = HorizontalAlignment.Left }, "Choice");
        var apply = Id(new Button { Content = "적용", Width = 120, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 6) }, "Apply");
        apply.Click += (_, _) => status.Text = "적용됨: " + choice.SelectedItem;
        var slow = Id(new Button { Content = "지연 적용", Width = 120, HorizontalAlignment = HorizontalAlignment.Left }, "Slow");
        slow.Click += (_, _) => { status.Text = "처리 중"; var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) }; timer.Tick += (_, _) => { timer.Stop(); status.Text = "지연 완료"; }; timer.Start(); };
        var tri = Id(new CheckBox { Content = "세 상태", IsThreeState = true, IsChecked = false, Margin = new Thickness(0, 6, 0, 6) }, "TriState");
        var volume = Id(new Slider { Minimum = 0, Maximum = 100, Value = 10, Width = 200, HorizontalAlignment = HorizontalAlignment.Left }, "Volume");
        var name = Id(new TextBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 6) }, "NameBox");
        var list = Id(new ListBox { Height = 200, ItemsSource = Enumerable.Range(0, 500).Select(i => "항목 " + i.ToString("D3")).ToArray() }, "Items");
        VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        foreach (var e in new UIElement[] { choice, apply, slow, tri, volume, name, status, list }) panel.Children.Add(e);
        root.Children.Add(panel); window.Content = root; app.Run(window);
    }
}

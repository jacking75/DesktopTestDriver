using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Microsoft.Win32;
internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var app = new Application(); var window = new Window { Title = "간단 메모", Width = 740, Height = 540 };
        var panel = new DockPanel(); var bar = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(bar, Dock.Top); panel.Children.Add(bar);
        var edit = new TextBox { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; AutomationProperties.SetAutomationId(edit, "MemoText");
        string? current = null;
        void Load(string path) { edit.Text = File.ReadAllText(path); current = path; window.Title = "간단 메모 - " + Path.GetFileName(path); }
        var open = new Button { Content = "열기", Padding = new Thickness(18, 8, 18, 8) }; AutomationProperties.SetAutomationId(open, "OpenButton");
        var save = new Button { Content = "저장", Padding = new Thickness(18, 8, 18, 8) }; AutomationProperties.SetAutomationId(save, "SaveButton");
        open.Click += (_, _) => { var d = new OpenFileDialog { Filter = "텍스트 파일|*.txt|모든 파일|*.*" }; if (d.ShowDialog(window) == true) try { Load(d.FileName); } catch (Exception e) { MessageBox.Show(window, e.Message); } };
        save.Click += (_, _) => { var d = new SaveFileDialog { Filter = "텍스트 파일|*.txt", FileName = current == null ? "memo.txt" : Path.GetFileName(current), OverwritePrompt = true }; if (d.ShowDialog(window) == true) try { File.WriteAllText(d.FileName, edit.Text, new System.Text.UTF8Encoding(false)); current = d.FileName; window.Title = "간단 메모 - " + Path.GetFileName(current); } catch (Exception e) { MessageBox.Show(window, e.Message); } };
        bar.Children.Add(open); bar.Children.Add(save); panel.Children.Add(edit); window.Content = panel;
        if (args.Length == 1) try { Load(args[0]); } catch (Exception e) { MessageBox.Show(e.Message); }
        app.Run(window);
    }
}

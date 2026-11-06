using System.IO;
using System.Windows.Forms;

namespace FormsWorkbench;
internal static class Program
{
    [STAThread] static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize(); Application.Run(new Workbench(args.Contains("--quiet")));
    }
}
class QuietForm(bool quiet) : Form
{
    protected override bool ShowWithoutActivation => quiet;
    protected override CreateParams CreateParams { get { var p=base.CreateParams; if(quiet) p.ExStyle|=0x08000000; return p; } }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    protected override void OnShown(EventArgs e) { base.OnShown(e); if(quiet) SetWindowPos(Handle,new IntPtr(1),0,0,0,0,0x13); }
}
sealed class Workbench : QuietForm
{
    public Workbench(bool quiet) : base(quiet)
    { Text = "독립 GUI 검증 도구"; Name = "FormsWorkbench"; Width = 660; Height = 700;
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        Controls.Add(panel);
        var editor = new QuietTextBox(quiet) { Name = "Editor", AccessibleName = "편집", Width = 450, Multiline = true, Height = 65 };
        var result = new QuietTextBox(quiet) { Name = "Result", AccessibleName = "처리 결과", ReadOnly = true, Width = 450 };
        var apply = new QuietButton(quiet) { Name = "Apply", Text = "적용" }; apply.Click += (_, _) => result.Text = editor.Text;
        panel.Controls.AddRange([editor, apply, result,
            new CheckBox { Name = "Option", Text = "옵션" },
            new ComboBox { Name = "Choice", Items = { "첫째", "둘째", "셋째" }, DropDownStyle = ComboBoxStyle.DropDownList },
            new QuietButton(quiet) { Text = "이름 없는 버튼", AccessibleName = "이름 없는 버튼" },
            new QuietButton(quiet) { Text = "중복" }, new QuietButton(quiet) { Text = "중복" },
            new QuietButton(quiet) { Name = "Disabled", Text = "비활성", Enabled = false },
            new TextBox { Name = "Password", UseSystemPasswordChar = true, Text = "fixture-secret" }]);
        var child = new QuietButton(quiet) { Name = "OpenChild", Text = "보조 창" };
        child.Click += (_, _) => new QuietForm(quiet) { Name = "Child", Text = "보조 창", Width = 300, Height = 200,StartPosition=FormStartPosition.Manual,Location=new System.Drawing.Point(Left+20,Top+20) }.Show(this);
        var modal = new QuietButton(quiet) { Name = "OpenModal", Text = "모달 창" };
        modal.Click += (_, _) => { using var dialog = new QuietForm(quiet) { Name = "Modal", Text = "모달 창", Width = 300, Height = 200,StartPosition=FormStartPosition.Manual,Location=new System.Drawing.Point(Left+20,Top+20) }; dialog.ShowDialog(this); };
        var save = new QuietButton(quiet) { Name = "Save", Text = "파일 저장" };
        save.Click += (_, _) => { using var dialog = new SaveFileDialog(); if (dialog.ShowDialog(this) == DialogResult.OK) File.WriteAllText(dialog.FileName, editor.Text); };
        var replace = new QuietButton(quiet) { Name = "Replace", Text = "요소 교체" };
        replace.Click += (_, _) => { panel.Controls.Remove(editor); editor.Dispose(); panel.Controls.Add(new TextBox { Name = "Editor", AccessibleName = "새 편집", Width = 450 }); };
        var failure = new QuietButton(quiet) { Name = "Failure", Text = "실패 재현" }; failure.Click += (_, _) => result.Text = "failed";
        panel.Controls.AddRange([child, modal, save, replace, failure]);
    }
}

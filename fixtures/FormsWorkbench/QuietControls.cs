using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Forms;

namespace FormsWorkbench;
sealed class QuietTextBox : TextBox
{
    public QuietTextBox(bool quiet=true) { if(quiet) {SetStyle(ControlStyles.Selectable,false); TabStop=false;} }
    private QuietProvider? provider;
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x3d && m.LParam.ToInt64() == AutomationInteropProvider.RootObjectId)
        { provider ??= new(this); m.Result = AutomationInteropProvider.ReturnRawElementProvider(Handle, m.WParam, m.LParam, provider); return; }
        base.WndProc(ref m);
    }
}
sealed class QuietButton : Button
{
    public void InvokeSemantically() => OnClick(EventArgs.Empty);
    public QuietButton(bool quiet=true) { if(quiet) {SetStyle(ControlStyles.Selectable,false); TabStop=false;} }
    private QuietProvider? provider;
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x3d && m.LParam.ToInt64() == AutomationInteropProvider.RootObjectId)
        { provider ??= new(this); m.Result = AutomationInteropProvider.ReturnRawElementProvider(Handle, m.WParam, m.LParam, provider); return; }
        base.WndProc(ref m);
    }
}
[ComVisible(true)]
public sealed class QuietProvider(Control owner) : IRawElementProviderSimple, IValueProvider, IInvokeProvider
{
    private T Read<T>(Func<T> read) => owner.InvokeRequired ? (T)owner.Invoke(read) : read();
    private void Write(Action write) { if (owner.InvokeRequired) owner.Invoke(write); else write(); }
    public ProviderOptions ProviderOptions => ProviderOptions.ServerSideProvider | ProviderOptions.UseComThreading | ProviderOptions.ProviderOwnsSetFocus;
    public IRawElementProviderSimple HostRawElementProvider => AutomationInteropProvider.HostProviderFromHandle(owner.Handle);
    public object? GetPatternProvider(int id) => id == ValuePatternIdentifiers.Pattern.Id && owner is TextBox || id == InvokePatternIdentifiers.Pattern.Id && owner is Button ? this : null;
    public object? GetPropertyValue(int id) => id switch
    {
        30003 => owner is TextBox ? ControlType.Edit.Id : ControlType.Button.Id,
        30005 => Read(() => owner.AccessibleName ?? owner.Text),
        30009 => Read(() => owner.CanFocus),
        30010 => Read(() => owner.Enabled),
        30011 => Read(() => owner.Name),
        30012 => owner.GetType().Name,
        30013 => "fixture",
        30019 => Read(() => owner is TextBox t && t.UseSystemPasswordChar),
        30022 => Read(() => !owner.Visible),
        30024 => "WinForm",
        _ => null
    };
    public bool IsReadOnly => Read(() => owner is not TextBox t || t.ReadOnly);
    public string Value => Read(() => owner.Text);
    public void SetValue(string value) => Write(() => { if (!owner.Enabled || IsReadOnly) throw new InvalidOperationException(); owner.Text = value; });
    public void Invoke()
    {
        if(!Read(()=>owner.Enabled) || owner is not Button button)throw new InvalidOperationException();
        owner.BeginInvoke(()=>{if(button is QuietButton quietButton)quietButton.InvokeSemantically();else button.PerformClick();});
    }
}

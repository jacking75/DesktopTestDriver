using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

sealed class QuietTextBox : TextBox
{
    public QuietTextBox(bool quiet=false) { Focusable=!quiet; }
    protected override AutomationPeer OnCreateAutomationPeer() => new QuietPeer(this);
}
sealed class QuietPeer(QuietTextBox owner) : TextBoxAutomationPeer(owner), IValueProvider
{
    protected override bool IsKeyboardFocusableCore() => owner.Focusable && base.IsKeyboardFocusableCore();
    protected override void SetFocusCore() { if(owner.Focusable) base.SetFocusCore(); }
    public override object GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.Value ? this : base.GetPattern(patternInterface);
    bool IValueProvider.IsReadOnly => owner.IsReadOnly;
    string IValueProvider.Value => owner.Text;
    void IValueProvider.SetValue(string value) { if (owner.IsReadOnly || !owner.IsEnabled) throw new InvalidOperationException(); owner.Text = value; }
}

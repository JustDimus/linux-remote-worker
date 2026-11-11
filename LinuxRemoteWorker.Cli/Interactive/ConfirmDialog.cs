using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace LinuxRemoteWorker.Cli.Interactive;

/// <summary>
/// Asks before something that cannot be taken back, in the error colours. The safe answer is focused and is the
/// default, so Enter or Esc never says yes by accident; yes takes a deliberate move (Tab, or its Alt hot key).
/// MessageBox is not used for this: it focuses the first button, which would be the risky one.
/// </summary>
internal sealed class ConfirmDialog : Dialog
{
    private readonly Button _safe;

    private ConfirmDialog(string title, string message, string yes, string no)
    {
        Title = title;
        Width = Dim.Percent(70);
        SchemeName = SchemeManager.SchemesToSchemeName(Schemes.Error);

        var text = Labels.Wrapped(message);
        text.Y = 0;
        Add(text);

        AddButton(new Button { Text = yes });
        _safe = new Button { Text = no };
        AddButton(_safe); // the last button is the default one
        Arrows.ButtonRow(Buttons, () => null);
    }

    /// <returns>True only when the user chose <paramref name="yes"/>.</returns>
    public static bool Ask(IApplication app, string title, string message, string yes = "_Yes, run it", string no = "_No")
    {
        using var dialog = new ConfirmDialog(title, message, yes, no);
        app.Run(dialog);
        return dialog.Result == 0;
    }

    protected override void OnIsModalChanged(bool newIsModal)
    {
        base.OnIsModalChanged(newIsModal);
        if (newIsModal) _safe.SetFocus();
    }
}

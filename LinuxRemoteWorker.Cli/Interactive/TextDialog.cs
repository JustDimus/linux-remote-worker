using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// TextView is marked obsolete in favour of EditorView from Terminal.Gui.Editor, which has no stable release yet;
// TextView is still the stable read-only, scrollable, selectable text of Terminal.Gui 2.5
#pragma warning disable CS0618

namespace LinuxRemoteWorker.Cli.Interactive;

/// <summary>
/// Shows a longer text - a status, a log, a result, an error with its hint - to read, scroll and copy. The text has the
/// focus, so the arrows and Page Up / Down scroll it at once; Esc closes.
/// </summary>
internal static class TextDialog
{
    /// <param name="atEnd">Open at the end, where a log has its newest lines.</param>
    public static void Show(IApplication app, string title, string text, bool error = false, bool atEnd = false)
    {
        using var dialog = new Dialog { Title = title, Width = Dim.Percent(90), Height = Dim.Percent(80) };
        if (error) dialog.SchemeName = SchemeManager.SchemesToSchemeName(Schemes.Error);
        var view = new TextView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), ReadOnly = true, WordWrap = true, Text = text };
        dialog.Add(view);
        dialog.AddButton(new Button { Text = "_Close" });
        Arrows.ButtonRow(dialog.Buttons, () => view);
        dialog.IsModalChanged += (_, e) =>
        {
            if (!e.Value) return;
            view.SetFocus();
            if (atEnd) view.MoveEnd();
        };
        app.Run(dialog);
    }
}

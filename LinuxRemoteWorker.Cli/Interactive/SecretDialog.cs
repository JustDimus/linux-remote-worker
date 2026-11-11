using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace LinuxRemoteWorker.Cli.Interactive;

/// <summary>Asks for a password or a key passphrase the server needs; it is kept for this session only.</summary>
internal static class SecretDialog
{
    /// <returns>What was typed, or null when the user cancelled.</returns>
    public static string? Ask(IApplication app, string title)
    {
        using var dialog = new Dialog { Title = title };
        var note = new Label { X = 0, Y = 0, Text = "Used for this session only; never saved." };
        var field = new TextField { X = 0, Y = 2, Width = 48, Secret = true };
        dialog.Add(note, field);
        dialog.AddButton(new Button { Text = "Cancel" });
        dialog.AddButton(new Button { Text = "_OK" });
        Arrows.ButtonRow(dialog.Buttons, () => field);
        app.Run(dialog);
        return dialog.Result == 1 ? field.Text : null;
    }
}

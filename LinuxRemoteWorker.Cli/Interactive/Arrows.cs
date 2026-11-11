using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace LinuxRemoteWorker.Cli.Interactive;

/// <summary>
/// The arrows by where things are on the screen, for the buttons at the bottom of a dialog: Terminal.Gui's own arrows
/// follow the focus order instead, so Up on Run went to Cancel next to it, and Right from the last button wrapped
/// round to the first field.
/// </summary>
internal static class Arrows
{
    /// <summary>
    /// Left / Right along the buttons (skipping disabled ones), Up to what stands above them; at the ends, and on Down,
    /// the focus stays.
    /// </summary>
    public static void ButtonRow(IReadOnlyList<Button> buttons, Func<View?> above)
    {
        foreach (var button in buttons)
        {
            var current = button;
            current.KeyDown += (_, key) =>
            {
                var index = IndexOf(buttons, current);
                if (key == Key.CursorLeft) Focus(buttons, index, -1);
                else if (key == Key.CursorRight) Focus(buttons, index, +1);
                else if (key == Key.CursorUp) above()?.SetFocus();
                else if (key != Key.CursorDown) return;
                key.Handled = true;
            };
        }
    }

    private static void Focus(IReadOnlyList<Button> buttons, int from, int step)
    {
        for (var i = from + step; i >= 0 && i < buttons.Count; i += step)
        {
            if (!buttons[i].Enabled) continue;
            buttons[i].SetFocus();
            return;
        }
    }

    private static int IndexOf(IReadOnlyList<Button> buttons, Button button)
    {
        for (var i = 0; i < buttons.Count; i++)
            if (buttons[i] == button) return i;
        return -1;
    }
}

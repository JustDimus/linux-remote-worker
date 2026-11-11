using System.Text;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;

namespace LinuxRemoteWorker.Cli.Interactive;

/// <summary>
/// Characters every monospace font has. Terminal.Gui's defaults (rounded and heavy frames, ⟦ ⟧ buttons, ☑ boxes,
/// quarter-block shadows) are missing from Consolas, the font of the classic Windows console, which shows them as
/// empty squares. Set before any view is made: views read these defaults when they are created.
/// </summary>
internal static class Look
{
    public static void Apply()
    {
        GlyphSettings.Current = GlyphSettings.Current with
        {
            LeftBracket = new Rune('['),
            RightBracket = new Rune(']'),
            CheckStateChecked = new Rune('■'),
            CheckStateUnChecked = new Rune('□'),
            CheckStateNone = new Rune('-'),
            Selected = new Rune('●'),
            UnSelected = new Rune('○')
        };
        ButtonSettings.Current = ButtonSettings.Current with { DefaultShadow = ShadowStyles.None };
        DialogSettings.Current = DialogSettings.Current with { DefaultShadow = ShadowStyles.None, DefaultBorderStyle = LineStyle.Single };
        FrameViewSettings.Current = FrameViewSettings.Current with { DefaultBorderStyle = LineStyle.Single };
        WindowSettings.Current = WindowSettings.Current with { DefaultShadow = ShadowStyles.None, DefaultBorderStyle = LineStyle.Single };
    }
}

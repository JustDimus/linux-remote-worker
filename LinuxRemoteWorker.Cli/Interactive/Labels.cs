using System.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace LinuxRemoteWorker.Cli.Interactive;

/// <summary>
/// Labels for text that comes from elsewhere - descriptions, option names, output. Terminal.Gui reads an underscore
/// in a label as the mark of its hot key and hides it ("server_name" would show as "servername"); these keep it.
/// </summary>
internal static class Labels
{
    /// <summary>A hot key specifier no text contains (the noncharacter U+FFFF): no hot key at all.</summary>
    public static readonly Rune NoHotKey = new(0xFFFF);

    public static Label Plain(string text = "") => new() { Text = text, HotKeySpecifier = NoHotKey };

    /// <summary>A label as wide as its container and as high as its text, broken into lines at spaces.</summary>
    public static Label Wrapped(string text = "")
    {
        var label = new Label { X = 0, Width = Dim.Fill(), Height = Dim.Auto(DimAutoStyle.Text), Text = text, HotKeySpecifier = NoHotKey };
        label.TextFormatter.WordWrap = true;
        return label;
    }
}

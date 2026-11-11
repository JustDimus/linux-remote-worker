using System.Text;

namespace LinuxRemoteWorker.Cli.Interactive;

/// <summary>Remote output made fit for a text view: what a terminal would show, without the codes that drew it.</summary>
internal static class OutputText
{
    /// <summary>
    /// Colour and cursor codes removed, tabs expanded, other control characters dropped. A carriage return keeps only
    /// what was drawn over it, as on a terminal (apt's progress lines).
    /// </summary>
    public static string Clean(string line)
    {
        line = line.TrimEnd('\r', '\n');
        var cr = line.LastIndexOf('\r');
        if (cr >= 0) line = line[(cr + 1)..];

        var sb = new StringBuilder(line.Length);
        var state = Escape.None;
        foreach (var rune in line.EnumerateRunes())
        {
            var v = rune.Value;
            switch (state)
            {
                case Escape.Start:
                    state = v == '[' ? Escape.Csi : v == ']' ? Escape.Osc : Escape.None;
                    continue;
                case Escape.Csi:
                    if (v is >= 0x40 and <= 0x7E) state = Escape.None;
                    continue;
                case Escape.Osc:
                    if (v == 0x07) state = Escape.None;
                    else if (v == 0x1B) state = Escape.OscEnd;
                    continue;
                case Escape.OscEnd:
                    state = v == '\\' ? Escape.None : Escape.Osc;
                    continue;
            }

            if (v == 0x1B)
            {
                state = Escape.Start;
                continue;
            }
            if (v == '\t')
            {
                sb.Append(' ', 8 - sb.Length % 8);
                continue;
            }
            if (v < 0x20 || v is >= 0x7F and < 0xA0) continue;
            sb.Append(rune.ToString());
        }
        return sb.ToString();
    }

    private enum Escape { None, Start, Csi, Osc, OscEnd }
}

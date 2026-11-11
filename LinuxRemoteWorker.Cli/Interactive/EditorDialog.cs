using LinuxRemoteWorker.Cli.Framework;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// TextView is marked obsolete in favour of EditorView from Terminal.Gui.Editor, which has no stable release yet;
// TextView is still the stable multi-line editor of Terminal.Gui 2.5
#pragma warning disable CS0618

namespace LinuxRemoteWorker.Cli.Interactive;

/// <summary>
/// A file of the server, edited in place: a unit file, an nginx site, nginx.conf. It reads the file when it opens;
/// Save hands the text to the command that writes it (and reloads, tests or restarts what it belongs to). When that is
/// refused - nginx -t fails, the unit does not start - the reason shows under the text and the editor stays open, so
/// the mistake can be fixed.
/// </summary>
/// <remarks>
/// Reading and saving run in the background and a timer looks for their end, instead of awaiting them: a dialog may
/// be opened from inside a continuation the main loop is running, and continuations of background work cannot be
/// queued until that one returns.
/// </remarks>
internal sealed class EditorDialog : Dialog
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);

    private readonly string _title;
    private readonly TextView _editor;
    private readonly Label _status;
    private readonly Button _cancel;
    private readonly Button _save;
    private readonly Func<Task<InvocationResult>> _read;
    private readonly Func<string, Task<InvocationResult>> _write;
    private string _original = "";
    private bool _loaded;
    private Task<InvocationResult>? _saving;

    public EditorDialog(string title, string note, Func<Task<InvocationResult>> read, Func<string, Task<InvocationResult>> write)
    {
        _title = title;
        _read = read;
        _write = write;
        Title = title;
        Width = Dim.Percent(95);
        Height = Dim.Percent(90);

        var hint = Labels.Plain(note);
        hint.Width = Dim.Fill();

        // The reason a save was refused, under the text; the text gives up rows for it
        _status = Labels.Wrapped();
        _status.Y = Pos.AnchorEnd();
        _status.Height = Dim.Auto(DimAutoStyle.Text, maximumContentDim: 8);

        _editor = new TextView { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(_status), TabKeyAddsTab = false, ReadOnly = true };
        Add(hint, _editor, _status);

        _cancel = new Button { Text = "Cancel" };
        _save = new Button { Text = "_Save", Enabled = false };
        AddButton(_cancel);
        AddButton(_save);
        Arrows.ButtonRow(Buttons, () => _editor);
    }

    /// <summary>True once the text was written.</summary>
    public bool Saved { get; private set; }

    /// <summary>What the command that wrote it said.</summary>
    public string Message { get; private set; } = "";

    /// <summary>Why the file could not be read; null when it was.</summary>
    public string? ReadFailure { get; private set; }

    private string Edited => _editor.Text.Replace("\r\n", "\n");

    private bool Changed => _loaded && Edited.TrimEnd('\n') != _original.TrimEnd('\n');

    protected override void OnIsRunningChanged(bool newIsRunning)
    {
        base.OnIsRunningChanged(newIsRunning);
        if (!newIsRunning || _loaded || ReadFailure != null) return;

        Show("Reading the file...", Color.BrightYellow);
        var reading = _read();
        App!.AddTimeout(Poll, () =>
        {
            if (!reading.IsCompleted) return true;
            Loaded(reading.Result);
            return false;
        });
    }

    private void Loaded(InvocationResult result)
    {
        if (!result.Ok)
        {
            ReadFailure = ResultText.Summary(result);
            Show(ResultText.Render(result), Color.BrightRed);
            return;
        }

        _original = (result.Envelope["data"]?["content"]?.GetValue<string>() ?? "").Replace("\r\n", "\n");
        if (result.Envelope["data"]?["path"]?.GetValue<string>() is { } path) Title = $"{_title} - {path}";
        _editor.Text = _original;
        _editor.ReadOnly = false;
        _loaded = true;
        _save.Enabled = true;
        Show("", Color.White);
        _editor.SetFocus();
    }

    protected override bool OnAccepting(CommandEventArgs args)
    {
        View? source = null;
        args.Context?.Source?.TryGetTarget(out source);
        if (_saving != null) return true;
        if (source == _cancel) return base.OnAccepting(args);

        if (_loaded) Save();
        return true; // closes itself once the text is written
    }

    // Esc: leave without saving - after asking, when something was changed
    protected override bool OnIsRunningChanging(bool oldIsRunning, bool newIsRunning)
    {
        if (!newIsRunning && !Saved)
        {
            if (_saving != null) return true;
            if (Changed && !ConfirmDialog.Ask(App!, "Discard the changes?", "The text was changed and not saved.", yes: "_Discard", no: "_Keep editing"))
                return true;
        }
        return base.OnIsRunningChanging(oldIsRunning, newIsRunning);
    }

    private void Save()
    {
        // A file that ended with a newline keeps it, whatever the editor did with the last line
        var text = Edited;
        if (_original.EndsWith('\n') && !text.EndsWith('\n')) text += "\n";

        _save.Enabled = _cancel.Enabled = false;
        Show("Saving...", Color.BrightYellow);
        var saving = _saving = _write(text);
        App!.AddTimeout(Poll, () =>
        {
            if (!saving.IsCompleted) return true;
            Written(saving);
            return false;
        });
    }

    private void Written(Task<InvocationResult> saving)
    {
        _saving = null;
        _save.Enabled = _cancel.Enabled = true;
        if (saving.IsFaulted)
        {
            Show(saving.Exception!.GetBaseException().Message, Color.BrightRed);
            return;
        }
        if (!saving.Result.Ok)
        {
            Show(ResultText.Render(saving.Result), Color.BrightRed);
            return;
        }

        Saved = true;
        Message = ResultText.Summary(saving.Result);
        RequestStop();
    }

    private void Show(string text, Color color)
    {
        _status.Text = text;
        var scheme = _status.GetScheme();
        _status.SetScheme(new Scheme(scheme) { Normal = scheme.Normal with { Foreground = color } });
    }
}

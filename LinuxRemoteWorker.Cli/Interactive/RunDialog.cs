using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Core;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using CommandContext = LinuxRemoteWorker.Cli.Framework.CommandContext;

// TextView is marked obsolete in favour of EditorView from Terminal.Gui.Editor, which has no stable release yet;
// TextView is still the stable read-only, scrollable, selectable text of Terminal.Gui 2.5
#pragma warning disable CS0618

namespace LinuxRemoteWorker.Cli.Interactive;

/// <summary>What the user chose after a command finished.</summary>
internal enum RunNext { Close, Again, Edit }

/// <summary>
/// Runs one command: the command line, the server's output while it comes, then the result as the command line
/// prints it. Esc or Cancel asks the command to stop; most server commands only stop when they finish, so a second
/// try offers to leave lrw instead. Afterwards the command can run again, or go back to its form.
/// </summary>
internal sealed class RunDialog : Dialog
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);
    private static readonly string[] Spinner = ["|", "/", "-", "\\"];

    private readonly InteractiveApp _shell;
    private readonly CommandSpec _command;
    private readonly IReadOnlyDictionary<string, object?> _values;
    private readonly Label _status;
    private readonly TextView _output;
    private readonly Button? _edit;
    private readonly Button _again;
    private readonly Button _close;
    private readonly StringBuilder _text = new();
    private readonly List<string> _incoming = [];
    private readonly CancellationTokenSource _cancel = new();
    private readonly Stopwatch _clock = new();
    private CommandContext? _context;
    private Task<InvocationResult>? _task;
    private object? _timer;
    private bool _textChanged;
    private bool _finished;
    private bool _abandoned;
    private int _frame;

    public RunDialog(InteractiveApp shell, CommandSpec command, IReadOnlyDictionary<string, object?> values,
        string commandLine, bool canEdit, bool canRepeat)
    {
        _shell = shell;
        _command = command;
        _values = values;
        Title = $"lrw {command.FullPath}";
        Width = Dim.Percent(95);
        Height = Dim.Percent(90);

        _status = Labels.Plain("Starting...");
        _status.Width = Dim.Fill();
        _output = new TextView { X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill(), ReadOnly = true, WordWrap = true };
        Add(_status, _output);

        if (canEdit)
        {
            _edit = new Button { Text = "_Edit", Enabled = false };
            AddButton(_edit);
        }
        _again = new Button { Text = "Run _again", Enabled = false };
        _close = new Button { Text = "Cancel" };
        if (canRepeat) AddButton(_again);
        AddButton(_close);
        Arrows.ButtonRow(Buttons, () => _output);

        _text.Append("$ ").AppendLine(commandLine).AppendLine();
        _output.Text = _text.ToString();
        AppLog.Info($"---- {commandLine} (interactive) ----");
    }

    public RunNext Next { get; private set; } = RunNext.Close;

    /// <summary>The command's result once it finished; null when lrw was left while it still ran.</summary>
    public InvocationResult? Finished { get; private set; }

    // Until the result is on the screen
    private bool Running => _task != null && !_finished;

    protected override void OnIsRunningChanged(bool newIsRunning)
    {
        base.OnIsRunningChanged(newIsRunning);
        if (newIsRunning && _task == null) Start();
    }

    private void Start()
    {
        _context = new CommandContext(_shell.Session, line =>
        {
            lock (_incoming) _incoming.Add(OutputText.Clean(line));
        }, progressVisible: true);

        _clock.Start();
        _status.Text = "Running...";
        _task = _shell.InvokeAsync(_command, _context, _values, _cancel.Token);
        _timer = App!.AddTimeout(Tick, Update);
    }

    // On the UI thread a few times a second: new output, the clock, and the end of the command
    private bool Update()
    {
        if (_finished) return false;
        lock (_incoming)
        {
            foreach (var line in _incoming) _text.AppendLine(line);
            _textChanged |= _incoming.Count > 0;
            _incoming.Clear();
        }

        if (_task!.IsCompleted)
        {
            Finish(_task.Result);
            return false;
        }

        _frame++;
        _status.Text = _cancel.IsCancellationRequested
            ? $"{Spinner[_frame % 4]} Cancelling - {Elapsed} (most server commands only stop when they are done)"
            : $"{Spinner[_frame % 4]} Running - {Elapsed}    Esc: cancel";
        ShowText();
        return true;
    }

    private void Finish(InvocationResult result)
    {
        _finished = true;
        Finished = result;
        _clock.Stop();
        if (_context!.ProgressLineCount > 0) _text.AppendLine();
        _text.AppendLine(ResultText.Render(result, outputShown: _context.ProgressLineCount > 0));
        _textChanged = true;
        ShowText();

        var (text, color) = result switch
        {
            { Ok: true, ExitCode: 0 } => ($"Done in {Elapsed}", Color.BrightGreen),
            { Ok: true } => ($"Finished with exit code {result.ExitCode} in {Elapsed}", Color.BrightYellow),
            _ => ($"Failed ({result.Envelope["error"]?["kind"]?.GetValue<string>() ?? "error"}) after {Elapsed}", Color.BrightRed)
        };
        _status.Text = text;
        var scheme = _status.GetScheme();
        _status.SetScheme(new Scheme(scheme) { Normal = scheme.Normal with { Foreground = color } });

        _close.Text = "_Close";
        _again.Enabled = true;
        if (_edit != null) _edit.Enabled = true;
        _close.SetFocus();
    }

    private void ShowText()
    {
        if (!_textChanged) return;
        _textChanged = false;
        _output.Text = _text.ToString();
        _output.MoveEnd();
    }

    private string Elapsed => _clock.Elapsed.TotalSeconds < 60
        ? $"{_clock.Elapsed.TotalSeconds:0.0}s"
        : $"{(int)_clock.Elapsed.TotalMinutes}m {_clock.Elapsed.Seconds:00}s";

    protected override bool OnAccepting(CommandEventArgs args)
    {
        View? source = null;
        args.Context?.Source?.TryGetTarget(out source);

        if (Running)
        {
            if (source == _close) Cancel();
            return true; // nothing closes the window while the command runs
        }

        Next = source == _again ? RunNext.Again : source == _edit && _edit != null ? RunNext.Edit : RunNext.Close;
        return base.OnAccepting(args);
    }

    // Esc asks the dialog to stop: while the command runs, that means cancel it
    protected override bool OnIsRunningChanging(bool oldIsRunning, bool newIsRunning)
    {
        if (!newIsRunning && Running && !_abandoned)
        {
            Cancel();
            return true;
        }
        return base.OnIsRunningChanging(oldIsRunning, newIsRunning);
    }

    private void Cancel()
    {
        if (!_cancel.IsCancellationRequested)
        {
            _cancel.Cancel();
            return;
        }

        var leave = ConfirmDialog.Ask(App!, "Still running",
            "The server has not stopped the command yet. Keep waiting, or leave lrw? The command may still finish on the server.",
            yes: "_Leave lrw", no: "_Wait");
        if (!leave) return;

        _abandoned = true;
        _shell.Abandon();
        RequestStop();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_timer != null && !_finished) App?.RemoveTimeout(_timer);
            if (!_abandoned) _cancel.Dispose();
        }
        base.Dispose(disposing);
    }
}

using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json.Nodes;
using LinuxRemoteWorker.Cli.Framework;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace LinuxRemoteWorker.Cli.Interactive;

/// <summary>
/// Every command of lrw, searchable: whatever the module screens have no button for. Typing anywhere in the window
/// lands here. Enter opens the command's form, then runs it.
/// </summary>
internal sealed class CommandsPanel : Panel
{
    private readonly TextField _search;
    private readonly ListView _list;
    private readonly Label _details;
    private List<CommandSpec> _shown = [];

    public CommandsPanel(InteractiveApp shell) : base(shell)
    {
        var searchLabel = Labels.Plain("Search:");
        searchLabel.Y = 0;
        _search = new TextField { X = Pos.Right(searchLabel) + 1, Y = 0, Width = Dim.Fill() };

        // Typing goes to the search, not to the list's jump-to-item
        _list = new ListView { Width = Dim.Fill(), Height = Dim.Fill(), KeystrokeNavigator = null };
        var list = Section("Commands - Enter opens one", _list);
        list.Y = 1;
        list.Height = Dim.Percent(55);

        _details = Labels.Wrapped();
        _details.Y = 0;
        _details.Height = Dim.Fill();
        var about = Section("About", _details);
        about.Y = Pos.Bottom(list);
        about.Height = Dim.Fill(1);
        Add(searchLabel, _search, list, about);

        _search.TextChanged += (_, _) => ShowList();
        _search.KeyDown += (_, key) =>
        {
            if (key == Key.CursorDown) _list.SetFocus();
            else if (key == Key.Enter) OpenSelected();
            else if (key == Key.Esc && _search.Text.Length > 0) _search.Text = "";
            else return;
            key.Handled = true;
        };
        _list.ValueChanged += (_, _) => ShowDetails();
        _list.Accepted += (_, _) => OpenSelected();
        _list.KeyDown += (_, key) =>
        {
            // Up from the first command goes to the search above it; Down from the search comes back
            if (key == Key.CursorUp && (_list.SelectedItem ?? 0) <= 0)
            {
                _search.SetFocus();
                _search.MoveEnd();
                key.Handled = true;
            }
            else if (MainWindow.AtEdge(_list, key)) key.Handled = true;
            else if (MainWindow.TypedChar(key) is { } c)
            {
                Type(c);
                key.Handled = true;
            }
        };
        _list.RowRender += (_, row) =>
        {
            if (row.Row >= _shown.Count || !_shown[row.Row].Destructive) return;
            var scheme = _list.GetScheme();
            row.RowAttribute = row.Row == _list.SelectedItem
                ? scheme.Focus with { Foreground = Color.BrightRed }
                : scheme.Normal with { Foreground = Color.BrightRed };
        };
        ShowList();
    }

    public override void Enter()
    {
        if (_shown.Count > 0) _list.SetFocus();
        else _search.SetFocus();
    }

    /// <summary>Starts (or goes on with) a search from a key typed elsewhere in the window.</summary>
    public void Type(string text)
    {
        _search.SetFocus();
        _search.Text += text;
        _search.MoveEnd();
    }

    protected override Task<bool> LoadAsync()
    {
        ShowDetails();
        return Task.FromResult(true);
    }

    private void ShowList()
    {
        var words = _search.Text.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Matches(string text) => words.All(text.ToLowerInvariant().Contains);
        _shown = Shell.Groups.SelectMany(g => g.Commands)
            .Where(c => Matches($"{c.FullPath} {c.Description}"))
            .OrderBy(c => words.Length > 0 && !Matches(c.FullPath) ? 1 : 0) // matches in the name first
            .ToList();
        var width = _shown.Count == 0 ? 0 : Math.Min(_shown.Max(c => c.FullPath.Length), 30);
        _list.SetSource(new ObservableCollection<string>(_shown.Select(c => $"{c.FullPath.PadRight(width)}  {MainWindow.FirstSentence(c.Description)}")));
        if (_shown.Count > 0) _list.SelectedItem = 0;
        ShowDetails();
    }

    private CommandSpec? Selected => _list.SelectedItem is { } i && i < _shown.Count ? _shown[i] : null;

    private void ShowDetails()
    {
        if (Selected is not { } c)
        {
            _details.Text = _search.Text.Length > 0 ? "Nothing matches - Esc clears the search." : "";
            return;
        }

        var text = new StringBuilder($"lrw {c.FullPath}\n\n{c.Description}\n\n");
        if (c.Destructive) text.AppendLine("Destructive: asks before it runs.");
        if (c.Attribute.Unrestricted) text.AppendLine("Unrestricted: it can change anything - check what you type.");
        if (c.Attribute.ReadOnly) text.AppendLine("Read-only: changes nothing.");
        if (c.Attribute.Local) text.AppendLine("Runs on this computer; no SSH connection.");
        else if (!Shell.IsConnected) text.AppendLine("Runs on a server: connect to one first (Connection).");
        if (c.Attribute.Streams) text.AppendLine("Shows the server's output while it runs; can take minutes.");
        if (c.Attribute.Example is { } example) text.AppendLine($"Example: lrw {example}");
        text.Append(c.Parameters.Any(p => !p.IsConfirm) ? "Enter: fill in the form and run it" : "Enter: run it");
        _details.Text = text.ToString();
    }

    private void OpenSelected()
    {
        if (Selected is { } command) Shell.Open(command);
    }
}

/// <summary>This computer's own log of lrw and the desktop app: what went wrong, and when.</summary>
internal sealed class AppLogPanel : TablePanel
{
    private readonly DataTable _files = new(
        ("File", r => J.Str(r["name"])),
        ("Written by", r => J.Str(r["source"])),
        ("Size", r => r["size"] is JsonValue v && v.TryGetValue<long>(out var bytes) ? $"{bytes / 1024.0:0.#} KB" : ""),
        ("Changed", r => J.Str(r["modified"]).Replace('T', ' ')[..Math.Min(16, J.Str(r["modified"]).Length)]));

    public AppLogPanel(InteractiveApp shell) : base(shell)
    {
        Func<Task> OnFile(Func<string, Task> action) => OnRow(_files, "name", "a log file", action);

        Layout(
            Row(RefreshButton(), Button("Where are they?", () => Act("applog path", style: ActStyle.Show))),
            null, "", _files, "Log files, newest first",
            Row(
                Button("Read", OnFile(file => Act("applog read", With("file", file), ActStyle.Log))),
                Button("Errors only", OnFile(file => Act("applog read", With(("file", file), ("filter", "ERROR")), ActStyle.Log)))));

    }

    protected override async Task<bool> LoadAsync()
    {
        if (await Read("applog list") is not { } data) return false;
        _files.Show(data["files"]);
        return true;
    }
}

using System.Drawing;
using System.Globalization;
using System.Text.Json.Nodes;
using LinuxRemoteWorker.Core;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Color = Terminal.Gui.Drawing.Color;

namespace LinuxRemoteWorker.Cli.Interactive;

internal enum Mood { Plain, Busy, Ok, Bad }

/// <summary>
/// The right side of the main window for one module: what the server has (facts, tables) and buttons that act on it.
/// It loads its data the first time it is shown and again after every action; Refresh reloads it. The status line at
/// the bottom says what is happening and how the last action went.
/// </summary>
internal abstract class Panel : View
{
    private readonly Label _status;
    private readonly HashSet<char> _hotKeys = [];
    private bool _loaded;

    protected Panel(InteractiveApp shell)
    {
        Shell = shell;
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;

        _status = Labels.Plain();
        _status.X = 0;
        _status.Y = Pos.AnchorEnd(1);
        _status.Width = Dim.Fill();
        Add(_status);
    }

    protected InteractiveApp Shell { get; }

    /// <summary>A command of this panel is running; buttons wait for it.</summary>
    protected bool Busy { get; private set; }

    /// <summary>Puts the focus on what the panel is about (its table), when the user goes into it from the modules.</summary>
    public virtual void Enter() => SetFocus();

    /// <summary>Called whenever the panel comes on screen; the first time it loads what it shows.</summary>
    public void Shown()
    {
        if (_loaded) return;
        _loaded = true;
        Refresh();
    }

    public void Refresh() => Run(async () =>
    {
        Say("Loading...", Mood.Busy);
        if (await LoadAsync()) Say("");
    });

    /// <summary>Reads the server and fills the panel; false when something could not be read (the status says why).</summary>
    protected abstract Task<bool> LoadAsync();

    /// <summary>The data of a read-only command; null when it failed, with the reason on the status line.</summary>
    protected async Task<JsonNode?> Read(string command, params (string Name, object? Value)[] values)
    {
        var result = await Shell.QueryAsync(command, values.ToDictionary(v => v.Name, v => v.Value));
        if (result.Ok) return result.Envelope["data"] ?? new JsonObject();
        Say(ResultText.Summary(result), Mood.Bad);
        return null;
    }

    /// <summary>
    /// Runs a command for a button (see <see cref="InteractiveApp.ActAsync"/>), then reloads the panel and shows how
    /// it went.
    /// </summary>
    protected async Task Act(string command, IReadOnlyDictionary<string, string>? given = null, ActStyle style = ActStyle.Quick)
    {
        Say($"Running lrw {command}...", Mood.Busy);
        var result = await Shell.ActAsync(command, given, style);
        if (result == null)
        {
            Say("");
            return;
        }
        await LoadAsync();
        Say(result.Message, result.Ok ? Mood.Ok : Mood.Bad);
    }

    /// <summary>Edits a server file in place: <paramref name="show"/> reads it, <paramref name="set"/> writes it.</summary>
    protected async Task Edit(string title, string show, string set, string note, params (string Name, object? Value)[] key)
    {
        Say("Reading the file...", Mood.Busy);
        var result = await Shell.EditAsync(title, show, set, note, key.ToDictionary(k => k.Name, k => k.Value));
        if (result == null)
        {
            Say("");
            return;
        }
        await LoadAsync();
        Say(result.Message, result.Ok ? Mood.Ok : Mood.Bad);
    }

    /// <summary>
    /// A button that runs its work unless another command of the panel is still running. Its Alt hot key is the first
    /// letter of its text that no other button of the panel has yet.
    /// </summary>
    protected Button Button(string text, Func<Task> work)
    {
        var button = new Button { Text = HotKeyed(text) };
        if (!button.Text.Contains('_')) button.HotKeySpecifier = Labels.NoHotKey;
        button.Accepting += (_, e) =>
        {
            e.Handled = true;
            Run(work);
        };
        return button;
    }

    private string HotKeyed(string text)
    {
        for (var i = 0; i < text.Length; i++)
            if (char.IsAsciiLetter(text[i]) && _hotKeys.Add(char.ToLowerInvariant(text[i])))
                return text.Insert(i, "_");
        return text;
    }

    /// <summary>Buttons side by side, going on in the next line when the width runs out.</summary>
    protected static View Row(params View[] items) => new ButtonFlow(items);

    /// <summary>A framed part of the panel.</summary>
    protected static FrameView Section(string title, params View[] content)
    {
        // A title is no hot key: the letters belong to the buttons. A frame view is a tab group of its own, which Tab
        // and the arrows never enter (only F6 does); as a plain tab stop its table is reached like the buttons around it
        var frame = new FrameView
        {
            Title = title, X = 0, Width = Dim.Fill(), HotKeySpecifier = Labels.NoHotKey, TabStop = TabBehavior.TabStop,
            CanFocus = content.Any(c => c.CanFocus) // facts alone are nothing to stop at
        };
        frame.Add(content);
        return frame;
    }

    /// <summary>A label of "key  value" lines, as high as its text.</summary>
    protected static Label FactsLabel()
    {
        var label = Labels.Wrapped();
        label.Y = 0;
        return label;
    }

    protected async void Run(Func<Task> work)
    {
        if (Busy)
        {
            Say("Wait for the running command to finish.", Mood.Busy);
            return;
        }
        Busy = true;
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            AppLog.Error($"lrw interactive: {GetType().Name}", ex);
            Say(ex.Message, Mood.Bad);
        }
        finally
        {
            Busy = false;
        }
    }

    protected void Say(string text, Mood mood = Mood.Plain)
    {
        _status.Text = text;
        var scheme = _status.SuperView?.GetScheme() ?? _status.GetScheme();
        var color = mood switch
        {
            Mood.Busy => Color.BrightYellow,
            Mood.Ok => Color.BrightGreen,
            Mood.Bad => Color.BrightRed,
            _ => scheme.Normal.Foreground
        };
        _status.SetScheme(new Scheme(scheme) { Normal = scheme.Normal with { Foreground = color } });
    }

    /// <summary>The single value a selected row stands for, as a command argument.</summary>
    protected static Dictionary<string, string> With(string name, string value) => new() { [name] = value };

    protected static Dictionary<string, string> With(params (string Name, string Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value);
}

/// <summary>Buttons side by side, going on in the next line when the width runs out; as high as its lines.</summary>
internal sealed class ButtonFlow : View
{
    public ButtonFlow(IEnumerable<View> items)
    {
        X = 0;
        Width = Dim.Fill();
        CanFocus = true;
        Add(items.ToArray());
        Height = Dim.Func(_ => Place(SuperView?.Viewport.Width ?? 0, move: false));
    }

    protected override void OnSubViewLayout(LayoutEventArgs args)
    {
        Place(Viewport.Width, move: true);
        base.OnSubViewLayout(args);
    }

    // The arrows move on the screen, not along the focus order: Left / Right to the neighbouring button, Up / Down to
    // the button in the line above / below, or out of the row to what is above / below it. At the edges they stay.
    protected override bool OnKeyDown(Key key)
    {
        var buttons = SubViews.Where(v => v is { CanFocus: true, Visible: true, Enabled: true }).ToList();
        var index = buttons.FindIndex(b => b.HasFocus);
        if (index < 0) return base.OnKeyDown(key);
        var current = buttons[index].Frame;

        if (key == Key.CursorRight || key == Key.CursorLeft)
        {
            var sameLine = buttons.Where(b => b.Frame.Y == current.Y);
            var next = key == Key.CursorRight
                ? sameLine.Where(b => b.Frame.X > current.X).MinBy(b => b.Frame.X)
                : sameLine.Where(b => b.Frame.X < current.X).MaxBy(b => b.Frame.X);
            if (next != null) next.SetFocus();
            else Spatial.Move(this, key == Key.CursorRight ? Direction.Right : Direction.Left);
            return true;
        }
        if (key != Key.CursorDown && key != Key.CursorUp) return base.OnKeyDown(key);

        var down = key == Key.CursorDown;
        var line = buttons.Where(b => b.Frame.Y == current.Y + (down ? 1 : -1))
            .OrderBy(b => Math.Abs(b.Frame.X - current.X))
            .FirstOrDefault();
        if (line != null) line.SetFocus();
        else Spatial.Move(this, down ? Direction.Down : Direction.Up);
        return true;
    }

    // Lays the items out line by line in the width; returns how many lines that takes
    private int Place(int width, bool move)
    {
        if (width <= 0) width = int.MaxValue;
        int x = 0, line = 0;
        foreach (var item in SubViews)
        {
            // A button is drawn as "[ text ]"; its frame is not known before the first layout
            var size = item is Button b ? b.Text.Replace("_", "").Length + 4 : Math.Max(1, item.Frame.Width);
            if (x > 0 && x + size > width)
            {
                x = 0;
                line++;
            }
            if (move)
            {
                item.X = x;
                item.Y = line;
            }
            x += size + 1;
        }
        return line + 1;
    }
}

/// <summary>Moving the focus by where things are on the screen.</summary>
internal enum Direction { Up, Down, Left, Right }

internal static class Spatial
{
    /// <summary>
    /// Focuses the nearest thing in that direction that can take the focus - above or below, or beside it on the same
    /// line - looking among the view's neighbours and then their containers, up to the panel; false when there is none.
    /// </summary>
    public static bool Move(View view, Direction direction)
    {
        while (view.SuperView is { } parent)
        {
            var from = view.Frame;
            var candidates = parent.SubViews.Where(v => v != view && v is { CanFocus: true, Visible: true, Enabled: true }
                                                        && v.TabStop != TabBehavior.NoStop);
            bool SameLine(View v) => v.Frame.Y < from.Bottom && from.Y < v.Frame.Bottom;
            var target = direction switch
            {
                Direction.Down => candidates.Where(v => v.Frame.Y >= from.Bottom)
                    .OrderBy(v => v.Frame.Y).ThenBy(v => Math.Abs(v.Frame.X - from.X)).FirstOrDefault(),
                Direction.Up => candidates.Where(v => v.Frame.Bottom <= from.Y)
                    .OrderByDescending(v => v.Frame.Bottom).ThenBy(v => Math.Abs(v.Frame.X - from.X)).FirstOrDefault(),
                Direction.Right => candidates.Where(v => v.Frame.X >= from.Right && SameLine(v)).MinBy(v => v.Frame.X),
                _ => candidates.Where(v => v.Frame.Right <= from.X && SameLine(v)).MaxBy(v => v.Frame.Right)
            };
            if (target != null)
            {
                target.SetFocus();
                return true;
            }
            if (parent is Panel) return false; // never out of the module's screen
            view = parent;
        }
        return false;
    }
}

/// <summary>"key  value" lines with the keys lined up; facts without a value are left out.</summary>
internal static class Facts
{
    public static string Format(params (string Key, string? Value)[] facts)
    {
        var shown = facts.Where(f => !string.IsNullOrEmpty(f.Value)).ToList();
        if (shown.Count == 0) return "";
        var width = shown.Max(f => f.Key.Length);
        return string.Join("\n", shown.Select(f => $"{f.Key.PadRight(width)}  {f.Value!.Replace("\n", "\n" + new string(' ', width + 2))}"));
    }
}

/// <summary>Values of the commands' JSON as text for the screen.</summary>
internal static class J
{
    public static string Str(JsonNode? node) => node switch
    {
        null => "",
        JsonArray array => string.Join(", ", array.Select(Str)),
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "yes" : "no",
        _ => node.ToJsonString()
    };

    public static bool Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary>Lines of a string array, one per line.</summary>
    public static string Lines(JsonNode? node) =>
        node is JsonArray array ? string.Join("\n", array.Select(Str)) : Str(node);

    /// <summary>The date of an ISO timestamp, local time.</summary>
    public static string Date(JsonNode? node) =>
        DateTimeOffset.TryParse(Str(node), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? at.ToLocalTime().ToString("yyyy-MM-dd")
            : Str(node);

    public static string YesNo(JsonNode? node, string yes, string no) =>
        node is JsonValue v && v.TryGetValue<bool>(out var b) ? b ? yes : no : "";
}

/// <summary>
/// A table of a command's JSON array: one row per object (a plain value becomes the row's "value"), the columns
/// given as text. Keeps the selected row when the data is loaded again.
/// </summary>
internal sealed class DataTable
{
    private readonly Dictionary<string, Func<JsonObject, object>> _columns;
    private List<JsonObject> _rows = [];
    private ButtonFlow? _actions;
    private int _action;

    public DataTable(params (string Header, Func<JsonObject, string> Cell)[] columns)
    {
        _columns = columns.ToDictionary(c => c.Header, c => (Func<JsonObject, object>)(row => c.Cell(row)));
        View = new TableView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), FullRowSelect = true };
        View.Style.ExpandLastColumn = true;
        View.Style.ShowHorizontalBottomLine = false;
        Show(null);

        // Up on the first row goes to what is above the table, Down on the last to what is below it
        // The focus stays in the table: Up / Down pick the row, Left / Right the action (whole rows are selected, so
        // there are no columns to walk through), Enter runs it. Up on the first row goes to the menu above
        View.KeyDown += (_, key) =>
        {
            var row = View.Value?.SelectedCell.Y ?? 0;
            if (key == Key.CursorLeft) Pick(_action - 1);
            else if (key == Key.CursorRight) Pick(_action + 1);
            else if (key == Key.CursorUp && row <= 0) Spatial.Move(View, Direction.Up);
            else if (key == Key.CursorDown && row >= _rows.Count - 1) { } // nothing to go to below: the actions are picked with Left / Right
            else return;
            key.Handled = true;
        };
        View.Accepted += (_, _) => RunAction();
    }

    /// <summary>
    /// Makes the buttons under the table its actions: they act on the selected row, Left / Right in the table pick one
    /// (it is lit like a focused button), Enter or a double click runs it. They are no stops of their own; a click on
    /// one runs it too.
    /// </summary>
    public void UseActions(ButtonFlow actions)
    {
        _actions = actions;
        actions.TabStop = TabBehavior.NoStop;
        var buttons = Actions;
        for (var i = 0; i < buttons.Count; i++)
        {
            var index = i;
            buttons[i].TabStop = TabBehavior.NoStop;
            buttons[i].Accepting += (_, _) => Pick(index);
        }
        Pick(0);
    }

    /// <summary>The action Enter runs: the text of its button.</summary>
    public string? CurrentAction => Actions.ElementAtOrDefault(_action)?.Text.Replace("_", "");

    private List<Button> Actions => _actions?.SubViews.OfType<Button>().ToList() ?? [];

    private void Pick(int index)
    {
        var buttons = Actions;
        if (buttons.Count == 0) return;
        _action = Math.Clamp(index, 0, buttons.Count - 1);
        var scheme = _actions!.GetScheme();
        var lit = new Scheme(scheme) { Normal = scheme.Focus, HotNormal = scheme.HotFocus };
        for (var i = 0; i < buttons.Count; i++) buttons[i].SetScheme(i == _action ? lit : scheme);
    }

    private void RunAction() => Actions.ElementAtOrDefault(_action)?.InvokeCommand(Command.Accept);

    public TableView View { get; }

    public IReadOnlyList<JsonObject> Rows => _rows;

    public JsonObject? Selected =>
        View.Value is { } selection && selection.SelectedCell.Y >= 0 && selection.SelectedCell.Y < _rows.Count
            ? _rows[selection.SelectedCell.Y]
            : null;

    /// <summary>The text of a column of the selected row; null when no row is selected.</summary>
    public string? Pick(string field) => Selected is { } row ? J.Str(row[field]) : null;

    public void Show(JsonNode? rows)
    {
        var keep = View.Value?.SelectedCell.Y ?? 0;
        _rows = rows is JsonArray array
            ? array.Select(n => n as JsonObject ?? new JsonObject { ["value"] = n?.DeepClone() }).ToList()
            : [];
        View.Table = new EnumerableTableSource<JsonObject>(_rows, _columns);
        if (_rows.Count > 0) View.Value = new TableSelection(new Point(0, Math.Clamp(keep, 0, _rows.Count - 1)));
        View.SetNeedsDraw();
    }
}

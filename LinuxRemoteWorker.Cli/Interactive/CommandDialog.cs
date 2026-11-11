using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// TextView is marked obsolete in favour of EditorView from Terminal.Gui.Editor, which has no stable release yet;
// TextView is still the stable multi-line editor of Terminal.Gui 2.5
#pragma warning disable CS0618

namespace LinuxRemoteWorker.Cli.Interactive;

/// <summary>
/// The form of a command: a field for every parameter (text, a switch, a choice, a few lines for config text),
/// what the focused field is for, and the command line the form adds up to. Run (or Enter in a field) checks every
/// value the way the command line would and keeps the form open, with the reason, while one is wrong.
/// </summary>
internal sealed class CommandDialog : Dialog
{
    private const int ContentRows = 5;

    private readonly InteractiveApp _shell;
    private readonly CommandForm _form;
    private readonly Dictionary<FormField, View> _inputs = new();
    private readonly Label _help;
    private readonly Label _error;
    private readonly Label _preview;
    private readonly Button _cancel;
    private readonly Button _run;

    public CommandDialog(InteractiveApp shell, CommandForm form)
    {
        _shell = shell;
        _form = form;
        Title = $"lrw {form.Command.FullPath}";
        Width = Dim.Percent(90);

        var description = Labels.Wrapped(form.Command.Description);
        description.Y = 0;
        Add(description);
        _help = Labels.Wrapped();
        _error = Labels.Wrapped();
        _preview = Labels.Wrapped();

        var labelWidth = form.Fields.Max(f => f.Name.Length + (f.Required ? 2 : 0));
        View above = description;
        var gap = 1;
        foreach (var field in form.Fields)
        {
            var label = Labels.Plain(field.Required ? $"{field.Name} *" : field.Name);
            label.X = 0;
            label.Y = Pos.Bottom(above) + gap;
            var input = CreateInput(field);
            input.X = labelWidth + 2;
            input.Y = Pos.Top(label);
            input.HasFocusChanged += (_, e) =>
            {
                if (e.NewValue) _help.Text = field.Help;
            };
            Add(label, input);
            _inputs[field] = input;
            if (field.Kind != FieldKind.Content) UpDownBetweenFields(field, input);
            above = input;
            gap = 0;
        }

        _help.Y = Pos.Bottom(above) + 1;
        _error.Y = Pos.Bottom(_help);
        _preview.Y = Pos.Bottom(_error) + 1;
        Add(_help, _error, _preview);

        _cancel = new Button { Text = "Cancel" };
        _run = new Button { Text = form.Command.Destructive ? "Run..." : "_Run" };
        AddButton(_cancel);
        AddButton(_run);
        Arrows.ButtonRow(Buttons, () => _form.Fields.Count > 0 ? _inputs[_form.Fields[^1]] : null);

        UpdatePreview();
    }

    /// <summary>True when the form was submitted with valid values; false when it was cancelled.</summary>
    public bool Submitted { get; private set; }

    protected override void OnIsModalChanged(bool newIsModal)
    {
        base.OnIsModalChanged(newIsModal);
        if (!newIsModal) return;

        // Start where there is something to do: the first required field still empty, else Run
        var start = _form.Fields.FirstOrDefault(f => f.Error != null)
                    ?? _form.Fields.FirstOrDefault(f => f.Required && f.Given == null);
        if (start != null) _inputs[start].SetFocus();
        else _run.SetFocus();
        ShowErrors();
    }

    protected override bool OnAccepting(CommandEventArgs args)
    {
        View? source = null;
        args.Context?.Source?.TryGetTarget(out source);
        if (source != _cancel)
        {
            if (!_form.TryBuild(out _))
            {
                ShowErrors();
                if (_form.FirstError is { } wrong) _inputs[wrong].SetFocus();
                return true; // stays open
            }
            Submitted = true;
        }
        return base.OnAccepting(args);
    }

    private View CreateInput(FormField field)
    {
        switch (field.Kind)
        {
            case FieldKind.Toggle:
            {
                var box = new CheckBox { Text = "", Value = field.Toggled ? CheckState.Checked : CheckState.UnChecked };
                box.ValueChanged += (_, e) =>
                {
                    field.Toggled = e.NewValue == CheckState.Checked;
                    Changed(field);
                };
                return box;
            }
            case FieldKind.Choice:
            {
                var selector = new OptionSelector
                {
                    Orientation = Orientation.Horizontal,
                    Labels = field.Options.Select(o => o.Length == 0 ? "(not set)" : o).ToList(),
                    Value = Math.Max(0, IndexOf(field.Options, field.Value))
                };
                selector.ValueChanged += (_, e) =>
                {
                    field.Value = e.NewValue is { } i && i >= 0 && i < field.Options.Count ? field.Options[i] : "";
                    Changed(field);
                };
                return selector;
            }
            case FieldKind.Content:
            {
                var text = new TextView { Width = Dim.Fill(), Height = ContentRows, Text = field.Value, TabKeyAddsTab = false };
                text.ContentsChanged += (_, _) =>
                {
                    field.Value = text.Text;
                    Changed(field);
                };
                return text;
            }
            default:
            {
                var text = new TextField { Width = Dim.Fill(), Text = field.Value, Secret = field.Kind == FieldKind.Secret };
                text.TextChanged += (_, _) =>
                {
                    field.Value = text.Text;
                    Changed(field);
                };
                return text;
            }
        }
    }

    // A field takes one line: Left / Right work inside it (the cursor, the choice), Up / Down go to the field above or
    // below. A choice would otherwise take Up / Down for its options too. The lines of a content field keep them.
    private void UpDownBetweenFields(FormField field, View input)
    {
        void OnKey(object? sender, Key key)
        {
            // A choice changes with Left / Right at once, not only its cursor (which would need Space as well)
            if (input is OptionSelector selector && (key == Key.CursorLeft || key == Key.CursorRight))
            {
                var options = selector.SubViews.ToList();
                var choice = Math.Clamp((selector.Value ?? 0) + (key == Key.CursorRight ? 1 : -1), 0, options.Count - 1);
                selector.Value = choice;
                options[choice].SetFocus();
                key.Handled = true;
                return;
            }
            if (key != Key.CursorUp && key != Key.CursorDown) return;
            var next = IndexOfField(field) + (key == Key.CursorDown ? 1 : -1);
            if (next >= _form.Fields.Count) _run.SetFocus();
            else if (next >= 0) _inputs[_form.Fields[next]].SetFocus();
            key.Handled = true;
        }

        input.KeyDown += OnKey;
        foreach (var option in input.SubViews) option.KeyDown += OnKey; // a choice's options get the keys first
    }

    private int IndexOfField(FormField field)
    {
        for (var i = 0; i < _form.Fields.Count; i++)
            if (_form.Fields[i] == field) return i;
        return -1;
    }

    private void Changed(FormField field)
    {
        if (field.Error != null)
        {
            field.Error = null;
            ShowErrors();
        }
        UpdatePreview();
    }

    private void UpdatePreview() => _preview.Text = "$ " + _form.CommandLine(_shell.ChosenProfile, _shell.Windows);

    private void ShowErrors()
    {
        var errors = _form.Fields.Where(f => f.Error != null).Select(f => f.Error!).ToList();
        _error.Text = string.Join("\n", errors);
        if (errors.Count > 0)
        {
            var scheme = _error.GetScheme();
            _error.SetScheme(new Scheme(scheme) { Normal = scheme.Normal with { Foreground = Color.BrightRed } });
        }
    }

    private static int IndexOf(IReadOnlyList<string> options, string value)
    {
        for (var i = 0; i < options.Count; i++)
            if (options[i].Equals(value, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}

using System.Collections.ObjectModel;
using System.Text;
using LinuxRemoteWorker.Cli.Framework;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

#pragma warning disable CS0618 // TextView: see EditorDialog

namespace LinuxRemoteWorker.Cli.Interactive;

/// <summary>
/// The application window, laid out like the desktop app: on the left the server this session is connected to and the
/// modules, on the right the selected module's screen. The server's modules appear once a server is connected; before
/// that there are Connection, All commands and the app log. Esc goes back from a screen to the modules, and leaves
/// from there.
/// </summary>
internal sealed class MainWindow : Window
{
    private const string ConnectionModule = "Connection";
    private const string CommandsModule = "All commands";
    private const string FirstServerModule = "System";

    private sealed record Module(string Name, bool NeedsServer, string[] Needs, Func<InteractiveApp, Panel> Create);

    // In the order of the desktop app; a module shows only when lrw has the commands it reads
    private static readonly Module[] Modules =
    [
        new(ConnectionModule, false, ["connection test"], s => new ConnectionPanel(s)),
        new(FirstServerModule, true, ["system info"], s => new SystemPanel(s)),
        new("Services", true, ["services list"], s => new ServicesPanel(s)),
        new("Repositories", true, ["repos list"], s => new RepositoriesPanel(s)),
        new(".NET SDK", true, ["dotnet status"], s => new DotnetPanel(s)),
        new("PostgreSQL", true, ["pg status"], s => new PostgresPanel(s)),
        new("Nginx", true, ["nginx status"], s => new NginxPanel(s)),
        new("Certificates", true, ["certs status"], s => new CertificatesPanel(s)),
        new("Firewall", true, ["firewall status"], s => new FirewallPanel(s)),
        new("Security", true, ["security status"], s => new SecurityPanel(s)),
        new("Logs", true, ["logs apps"], s => new LogsPanel(s)),
        new(CommandsModule, false, [], s => new CommandsPanel(s)),
        new("App log", false, ["applog list"], s => new AppLogPanel(s))
    ];

    private readonly InteractiveApp _shell;
    private readonly Label _server;
    private readonly ListView _nav;
    private readonly FrameView _host;
    private readonly Dictionary<Module, Panel> _panels = new();
    private List<Module> _shown = [];
    private Panel? _current;
    private bool _started;

    public MainWindow(InteractiveApp shell)
    {
        _shell = shell;

        // The frames' titles are no hot keys: the letters belong to the buttons of the screens
        var left = new FrameView { Title = "lrw", X = 0, Y = 0, Width = 24, Height = Dim.Fill(1), CanFocus = true, HotKeySpecifier = Labels.NoHotKey };
        _server = Labels.Wrapped();
        _server.Y = 0;
        // Typing goes to the command search, not to the list's jump-to-item
        _nav = new ListView { Y = Pos.Bottom(_server) + 1, Width = Dim.Fill(), Height = Dim.Fill(), KeystrokeNavigator = null };
        left.Add(_server, _nav);

        _host = new FrameView { X = Pos.Right(left), Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1), CanFocus = true, HotKeySpecifier = Labels.NoHotKey };

        var back = new Shortcut(Application.GetDefaultKey(Command.Quit), "Back / quit", () => RequestStop(), "");
        var hints = new Shortcut(Key.Empty, "Enter/Tab open  ↑↓ row  ←→ action  / search  Alt+letter: button", null, "") { CanFocus = false };
        Add(left, _host, new StatusBar([back, hints]));


        _nav.ValueChanged += (_, _) => ShowSelected();
        _nav.Accepted += (_, _) => _current?.Enter();
        _nav.KeyDown += (_, key) =>
        {
            if (key == Key.Tab) _current?.Enter();
            else if (key == Key.CursorRight || key == Key.CursorLeft || AtEdge(_nav, key)) { } // the arrows stay in the list
            else if (TypedChar(key) is { } text) Search(text);
            else return;
            key.Handled = true;
        };

        _shell.ConnectionChanged += OnConnectionChanged;
        Rebuild(ConnectionModule);
        Initialized += (_, _) => _nav.SetFocus();
    }

    private Module? Selected => _nav.SelectedItem is { } i && i < _shown.Count ? _shown[i] : null;

    protected override void OnIsModalChanged(bool newIsModal)
    {
        base.OnIsModalChanged(newIsModal);
        if (!newIsModal || _started) return;
        _started = true;
        App!.Keyboard.KeyDown += OnAnyKey;

        // lrw --profile prod interactive: connect right away
        if (_shell.StartWith is { } options && Show(ConnectionModule) is ConnectionPanel connection) connection.ConnectTo(options);
    }

    // "/" (or Ctrl+F) searches every command from wherever the focus is. It is caught before any view sees it: a
    // table would take it for its own jump-to-row. Not in a dialog, and not in a text field, where it is text. Ctrl+F
    // alone would not do: VS Code's terminal keeps it for its own find.
    private void OnAnyKey(object? sender, Key key)
    {
        if (!IsSearchKey(key) || App?.TopRunnableView != this) return;
        if (MostFocused is TextField or TextView) return;
        Search("");
        key.Handled = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && App != null) App.Keyboard.KeyDown -= OnAnyKey;
        base.Dispose(disposing);
    }

    // Esc from a module's screen goes back to the modules; from the modules it leaves lrw
    protected override bool OnIsRunningChanging(bool oldIsRunning, bool newIsRunning)
    {
        if (!newIsRunning && _shell.ExitCode == 0 && _current is { HasFocus: true })
        {
            _nav.SetFocus();
            return true;
        }
        return base.OnIsRunningChanging(oldIsRunning, newIsRunning);
    }

    private void OnConnectionChanged()
    {
        // The screens of the previous server show its data: they start afresh for the next one
        foreach (var (module, panel) in _panels.Where(p => p.Key.NeedsServer).ToList())
        {
            if (panel == _current)
            {
                _host.Remove(panel);
                _current = null;
            }
            _panels.Remove(module);
            panel.Dispose();
        }

        Rebuild(_shell.IsConnected ? FirstServerModule : ConnectionModule);
        _nav.SetFocus();
    }

    private void Rebuild(string select)
    {
        _shown = Modules
            .Where(m => (!m.NeedsServer || _shell.IsConnected) && m.Needs.All(c => _shell.Find(c) != null))
            .ToList();
        _nav.SetSource(new ObservableCollection<string>(_shown.Select(m => m.Name)));
        var index = _shown.FindIndex(m => m.Name == select);
        _nav.SelectedItem = Math.Max(0, index);
        ShowSelected();
        ShowServer();
    }

    private void ShowSelected()
    {
        if (Selected is not { } module) return;
        if (!_panels.TryGetValue(module, out var panel)) _panels[module] = panel = module.Create(_shell);
        if (panel != _current)
        {
            if (_current != null) _host.Remove(_current);
            _current = panel;
            _host.Title = module.Name;
            _host.Add(panel);
        }
        panel.Shown();
    }

    /// <summary>Selects a module and returns its screen.</summary>
    private Panel? Show(string name)
    {
        var index = _shown.FindIndex(m => m.Name == name);
        if (index < 0) return null;
        _nav.SelectedItem = index;
        ShowSelected();
        return _current;
    }

    // At the top of the left side: which machine the commands run on
    private void ShowServer()
    {
        var scheme = _server.SuperView?.GetScheme() ?? _server.GetScheme();
        if (_shell.Server is { } server)
        {
            _server.Text = $"● {server.ProfileName ?? server.Host}\n  {server.Username}@{server.Host}";
            _server.SetScheme(new Scheme(scheme) { Normal = scheme.Normal with { Foreground = Color.BrightGreen } });
            Title = $"lrw {HelpWriter.Version} - {_shell.ServerLabel}";
        }
        else
        {
            _server.Text = "○ not connected";
            _server.SetScheme(new Scheme(scheme) { Normal = scheme.Normal with { Foreground = Color.BrightYellow } });
            Title = $"lrw {HelpWriter.Version} - Linux Remote Worker";
        }
    }

    private void Search(string text)
    {
        if (Show(CommandsModule) is CommandsPanel commands) commands.Type(text);
    }

    /// <summary>
    /// Up on the first item or Down on the last: a list that cannot move hands the key to the window, which moves the
    /// focus to the view above or below - out of sight, and Left and Right then no longer switch panes. At the ends
    /// the arrows stay in the list.
    /// </summary>
    internal static bool AtEdge(ListView list, Key key)
    {
        var selected = list.SelectedItem ?? 0;
        var last = (list.Source?.Count ?? 0) - 1;
        return (key == Key.CursorUp && selected <= 0) || (key == Key.CursorDown && selected >= last);
    }

    /// <summary>
    /// The printable character a key types; null for keys that move, act or carry Ctrl / Alt, and for "/", which opens
    /// the search instead of being searched for.
    /// </summary>
    internal static string? TypedChar(Key key)
    {
        if (key.IsCtrl || key.IsAlt || IsSearchKey(key)) return null;
        var rune = key.AsRune;
        return rune.Value < 0x20 || Rune.IsControl(rune) || Rune.IsWhiteSpace(rune) ? null : rune.ToString();
    }

    internal static bool IsSearchKey(Key key) =>
        key == Key.F.WithCtrl || (!key.IsCtrl && !key.IsAlt && key.AsRune.Value == '/');

    internal static string FirstSentence(string text)
    {
        var end = text.IndexOf(". ", StringComparison.Ordinal);
        return end < 0 ? text.TrimEnd('.') : text[..end];
    }
}

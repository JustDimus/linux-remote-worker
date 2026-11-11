using System.Text.Json.Nodes;
using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Cli.Session;
using LinuxRemoteWorker.Cli.Update;
using LinuxRemoteWorker.Core;
using Terminal.Gui.App;
using CommandContext = LinuxRemoteWorker.Cli.Framework.CommandContext;

namespace LinuxRemoteWorker.Cli.Interactive;

/// <summary>What the interactive mode reads from outside; the tests replace what would touch files or servers.</summary>
internal sealed class InteractiveOptions
{
    /// <summary>The server options of the command line (--profile, --host ...).</summary>
    public ConnectionOptions Connection { get; init; } = new();

    public Func<ConnectionOptions, ConnectionTarget> Resolve { get; init; } = TargetResolver.Resolve;
    public Func<List<ConnectionProfile>> LoadProfiles { get; init; } = () => new ProfileService().LoadStrict();
    public Func<string?> DefaultProfile { get; init; } = () => CliSettings.Load().DefaultProfile;

    /// <summary>Runs a command; the tests answer for the server here.</summary>
    public Func<CommandSpec, CommandContext, IReadOnlyDictionary<string, object?>, CancellationToken, Task<InvocationResult>> Invoke { get; init; } =
        CommandInvoker.InvokeAsync;

    /// <summary>Command lines are shown quoted for PowerShell rather than a POSIX shell.</summary>
    public bool Windows { get; init; } = OperatingSystem.IsWindows();
}

/// <summary>How a button runs its command.</summary>
[Flags]
internal enum ActStyle
{
    /// <summary>Run with what the screen knows (asking only for what is missing), report in the status line.</summary>
    Quick = 0,

    /// <summary>Open the command's form first, filled in with what the screen knows.</summary>
    Ask = 1,

    /// <summary>Show the whole result in a window (statuses, logs, generated passwords).</summary>
    Show = 2,

    /// <summary>A log: shown like <see cref="Show"/>, but at its end, where the newest lines are.</summary>
    Log = Show | 4
}

/// <summary>How a button's command went: the line for the status line.</summary>
internal sealed record ActResult(bool Ok, string Message);

/// <summary>
/// lrw interactive: the desktop app's screens in a terminal. Connect to a saved server, then each module shows what
/// the server has and acts on it with buttons; All commands reaches every other command through its form. Everything
/// runs through the same commands as the command line, one at a time over one SSH connection, and every action goes
/// to the app log with its command line.
/// </summary>
internal sealed class InteractiveApp : IDisposable
{
    public const string NotConnected = "Not connected to a server - connect to one under Connection first.";

    private static readonly IReadOnlyDictionary<string, object?> NoValues = new Dictionary<string, object?>();

    private readonly IApplication _app;
    private readonly InteractiveOptions _options;
    private readonly Dictionary<string, CommandForm> _forms = new();
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private MainWindow? _window;

    public InteractiveApp(IApplication app, CommandCatalog catalog, InteractiveOptions options)
    {
        _app = app;
        _options = options;
        Look.Apply();

        Groups = catalog.Groups
            .Select(g => g with { Commands = g.Commands.Where(c => c.On(Surface.Cli)).ToList() })
            .Where(g => g.Commands.Count > 0)
            .ToList();

        // Nothing runs on a server until one is connected under Connection
        Session = new ServerSession(() => throw new UsageException(NotConnected, "interactive"))
        {
            PromptPassword = server => AskSecret($"Password for {server}"),
            PromptPassphrase = () => AskSecret("Passphrase of the SSH key")
        };
        Session.Unselect(NotConnected, "interactive");
    }

    public IReadOnlyList<GroupSpec> Groups { get; }

    public ServerSession Session { get; }

    public bool Windows => _options.Windows;

    /// <summary>The process exit code: 0, or 130 when the user left while a command was still running.</summary>
    public int ExitCode { get; private set; }

    /// <summary>The server the command line named; the window connects to it at once.</summary>
    public ConnectionOptions? StartWith =>
        _options.Connection.Profile != null || _options.Connection.Host != null ? _options.Connection : null;

    /// <summary>
    /// Runs the interactive mode on the real terminal until the user leaves it. Refuses without one: scripts and
    /// agents call the commands themselves.
    /// </summary>
    public static async Task<int> RunAsync(CommandCatalog catalog, ConnectionOptions connection)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            await Console.Error.WriteLineAsync(
                "error: lrw interactive needs a terminal to draw on. Scripts and agents run the commands themselves (lrw help).");
            return ExitCodes.Invalid;
        }

        AppLog.CleanupOldFiles();
        AppLog.Info("---- lrw interactive ----");
        var notifier = UpdateNotifier.StartIfDue();

        int exitCode;
        try
        {
            using var app = Application.Create().Init();
            using var shell = new InteractiveApp(app, catalog, new InteractiveOptions { Connection = connection });
            exitCode = shell.Run();
        }
        catch (Exception ex)
        {
            AppLog.Error("lrw interactive failed", ex);
            await Console.Error.WriteLineAsync($"error: the interactive mode stopped: {ex.Message}");
            return ExitCodes.Failed;
        }

        // After the screen is restored, so the notice stays readable in the shell
        if (notifier != null) await notifier.FinishAsync(Console.Error);
        return exitCode;
    }

    /// <summary>Shows the main window until the user leaves.</summary>
    public int Run()
    {
        using var window = _window = new MainWindow(this);
        _app.Run(window);
        _window = null;
        return ExitCode;
    }

    // ── The server ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The server this session is connected to; null before Connect.</summary>
    public ConnectionTarget? Server { get; private set; }

    /// <summary>What the connection test found out about it.</summary>
    public JsonObject? ServerFacts { get; private set; }

    public bool IsConnected => Server != null;

    public string? ServerLabel => Server is not { } s ? null
        : s.ProfileName != null ? $"{s.ProfileName} ({s.Username}@{s.Host})" : $"{s.Username}@{s.Host}";

    /// <summary>The profile of the connection, for the command lines shown (--profile).</summary>
    public string? ChosenProfile => Server?.ProfileName;

    /// <summary>Raised when a server is connected or disconnected.</summary>
    public event Action? ConnectionChanged;

    public List<ConnectionProfile> LoadProfiles() => _options.LoadProfiles();

    public string? DefaultProfile() => _options.DefaultProfile();

    /// <summary>Logs in to a server and tests the connection; the session runs on it from then on.</summary>
    public async Task<InvocationResult> ConnectAsync(ConnectionOptions options)
    {
        ConnectionTarget target;
        try
        {
            target = _options.Resolve(options);
        }
        catch (Exception ex) when (ex is UsageException or OperationException)
        {
            return ResultText.Failure("connection test", ex.Message);
        }

        Session.Use(target);
        var result = await QueryAsync("connection test");
        if (result.Ok)
        {
            // The session's copy: it holds a password or passphrase typed while connecting
            Server = Session.Selected ?? target;
            ServerFacts = result.Envelope["data"] as JsonObject;
            AppLog.Info($"lrw interactive: connected to {Server.Display}");
        }
        else
        {
            Session.Unselect(NotConnected, "interactive");
            Server = null;
            ServerFacts = null;
        }
        ConnectionChanged?.Invoke();
        return result;
    }

    public void Disconnect()
    {
        if (Server == null) return;
        AppLog.Info($"lrw interactive: disconnected from {Server.Display}");
        Session.Unselect(NotConnected, "interactive");
        Server = null;
        ServerFacts = null;
        ConnectionChanged?.Invoke();
    }

    // ── Commands ───────────────────────────────────────────────────────────────────────────────────────────────

    public CommandSpec? Find(string fullPath) =>
        Groups.SelectMany(g => g.Commands).FirstOrDefault(c => c.FullPath == fullPath);

    /// <summary>The form of a command; it keeps what was typed for the whole session.</summary>
    public CommandForm FormFor(CommandSpec command) =>
        _forms.TryGetValue(command.FullPath, out var form) ? form : _forms[command.FullPath] = new CommandForm(command);

    /// <summary>
    /// Runs any command - one at a time, as the server session allows only one command at once. Commands the screens
    /// read their data with come through here, and so does everything else.
    /// </summary>
    /// <remarks>
    /// It finishes without the UI thread (ConfigureAwait(false)): a dialog that waits for it may be running inside a
    /// continuation of the main loop, which holds back every other continuation until it returns.
    /// </remarks>
    public async Task<InvocationResult> InvokeAsync(CommandSpec command, CommandContext context,
        IReadOnlyDictionary<string, object?> values, CancellationToken ct)
    {
        await _oneAtATime.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => _options.Invoke(command, context, values, ct), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>Runs a command without showing anything and returns its result.</summary>
    public Task<InvocationResult> QueryAsync(string path, IReadOnlyDictionary<string, object?>? values = null) =>
        Find(path) is { } command
            ? QueryAsync(command, values ?? NoValues)
            : Task.FromResult(ResultText.Failure(path, $"This lrw has no command '{path}'."));

    public Task<InvocationResult> QueryAsync(CommandSpec command, IReadOnlyDictionary<string, object?> values) =>
        InvokeAsync(command, new CommandContext(Session, _ => { }, progressVisible: false), values, CancellationToken.None);

    /// <summary>
    /// A button's command: filled in with what the screen knows (the selected service, site, database ...), its form
    /// shown when it needs more or the button asks for it, confirmed when it is destructive. A command that streams
    /// (installs, deploys) shows its output in the run window; others run in the background, and a failure, or a
    /// result worth reading, opens in a window. Null when the user cancelled.
    /// </summary>
    public async Task<ActResult?> ActAsync(string path, IReadOnlyDictionary<string, string>? given = null, ActStyle style = ActStyle.Quick)
    {
        if (Find(path) is not { } command) return new ActResult(false, $"This lrw has no command '{path}'.");

        // A quick action starts from the defaults: what was typed into its form earlier must not change what it does
        var ask = style.HasFlag(ActStyle.Ask);
        var form = ask ? FormFor(command) : new CommandForm(command);
        if (given != null)
            foreach (var (name, value) in given)
                form.Set(name, value);
        ask |= form.Fields.Any(f => f.Required && f.Given == null);

        Dictionary<string, object?> values;
        while (true)
        {
            if (ask)
            {
                using var dialog = new CommandDialog(this, form);
                _app.Run(dialog);
                if (!dialog.Submitted) return null;
            }
            if (form.TryBuild(out values)) break;
            ask = true; // a value the screen gave was refused: the form shows why
        }

        var commandLine = form.CommandLine(ChosenProfile, Windows);
        if (command.Destructive)
        {
            if (!Confirm(command, commandLine)) return null;
            values[CommandCatalog.ConfirmName] = true;
        }

        if (command.Attribute.Streams)
        {
            using var run = new RunDialog(this, command, values, commandLine, canEdit: false, canRepeat: false);
            _app.Run(run);
            return run.Finished is { } streamed
                ? new ActResult(streamed.Ok, ResultText.Summary(streamed))
                : new ActResult(false, "Stopped before it finished.");
        }

        AppLog.Info($"---- {commandLine} (interactive) ----");
        var result = await QueryAsync(command, values);
        if (!result.Ok || style.HasFlag(ActStyle.Show))
            TextDialog.Show(_app, $"{(result.Ok ? "" : "Failed: ")}lrw {command.FullPath}", ResultText.Render(result),
                error: !result.Ok, atEnd: result.Ok && style.HasFlag(ActStyle.Log));
        return new ActResult(result.Ok, ResultText.Summary(result));
    }

    /// <summary>
    /// Edits a server file in place: reads it with <paramref name="show"/> (data.content), opens it in the editor, and
    /// writes it with <paramref name="set"/> (--content) when saved. Null when the user left without saving.
    /// </summary>
    public Task<ActResult?> EditAsync(string title, string show, string set, string note,
        IReadOnlyDictionary<string, object?> key)
    {
        if (Find(set) is not { } setCommand)
            return Task.FromResult<ActResult?>(new ActResult(false, $"This lrw has no command '{set}'."));

        // The editor opens at once and reads the file itself (see EditorDialog)
        using var editor = new EditorDialog(title, note, () => QueryAsync(show, key), text =>
        {
            var values = new Dictionary<string, object?>(key) { ["content"] = text };
            var form = new CommandForm(setCommand);
            foreach (var (name, value) in values) form.Set(CommandCatalog.Kebab(name), value?.ToString() ?? "");
            AppLog.Info($"---- {form.CommandLine(ChosenProfile, Windows)} (interactive) ----");
            return QueryAsync(setCommand, values);
        });
        _app.Run(editor);
        return Task.FromResult(editor.Saved ? new ActResult(true, editor.Message)
            : editor.ReadFailure is { } failure ? new ActResult(false, failure)
            : null);
    }

    /// <summary>
    /// Asks for the command's input (when it takes any), confirms a destructive command, runs it and shows the result.
    /// From the result the user can run it again or go back to the form. What All commands opens.
    /// </summary>
    public void Open(CommandSpec command)
    {
        var form = FormFor(command);
        var showForm = form.Fields.Count > 0;
        while (ExitCode == 0)
        {
            if (showForm)
            {
                using var dialog = new CommandDialog(this, form);
                _app.Run(dialog);
                if (!dialog.Submitted) return;
            }

            if (!form.TryBuild(out var values)) return; // the dialog only closes with a valid form
            var commandLine = form.CommandLine(ChosenProfile, Windows);

            if (command.Destructive)
            {
                if (!Confirm(command, commandLine))
                {
                    if (form.Fields.Count == 0) return;
                    showForm = true;
                    continue;
                }
                values[CommandCatalog.ConfirmName] = true;
            }

            RunNext next;
            using (var run = new RunDialog(this, command, values, commandLine, canEdit: form.Fields.Count > 0, canRepeat: true))
            {
                _app.Run(run);
                next = run.Next;
            }

            if (next == RunNext.Close) return;
            showForm = next == RunNext.Edit;
        }
    }

    /// <summary>Leaves lrw although a command still runs (it could not be cancelled).</summary>
    public void Abandon()
    {
        ExitCode = ExitCodes.Cancelled;
        _window?.RequestStop();
    }

    private bool Confirm(CommandSpec command, string commandLine)
    {
        // Saved servers are this computer's own list; anything else happens on the server, so name it
        var server = command.Group == "profiles" ? "" : $"\nServer: {ServerLabel ?? "none connected"}\n";
        return ConfirmDialog.Ask(_app, "Destructive command", $"{command.Description}\n{server}\n{commandLine}\n\nRun it?");
    }

    // Called on the command's thread when the server wants a password or a passphrase: asks on the UI thread and waits
    private string? AskSecret(string label)
    {
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _app.Invoke(() =>
        {
            try
            {
                answer.TrySetResult(SecretDialog.Ask(_app, label));
            }
            catch (Exception ex)
            {
                answer.TrySetException(ex);
            }
        });
        return answer.Task.GetAwaiter().GetResult();
    }

    // The gate is not disposed: a command left running (Abandon) still releases it
    public void Dispose() => Session.Dispose();
}

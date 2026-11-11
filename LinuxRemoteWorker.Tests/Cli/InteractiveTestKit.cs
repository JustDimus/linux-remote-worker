using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Cli.Interactive;
using LinuxRemoteWorker.Cli.Session;
using LinuxRemoteWorker.Core;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Time;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using CommandContext = LinuxRemoteWorker.Cli.Framework.CommandContext;

namespace LinuxRemoteWorker.Tests.Cli;

/// <summary>Local commands for driving lrw interactive in tests: nothing here connects anywhere.</summary>
[CommandGroup("demo", "Commands for the interactive tests")]
public sealed class DemoCommands : CommandSet
{
    /// <summary>What ran, as "command argument" - tests use values of their own, so they can run side by side.</summary>
    public static readonly ConcurrentQueue<string> Calls = new();

    public DemoCommands(CommandContext context) : base(context) { }

    [Command("echo", "Return the text it got.", ReadOnly = true, Local = true)]
    public Outcome Echo(
        [Arg("Text to return", Positional = true)] string text,
        [Arg("How many times")] int times = 1,
        [Arg("Shout it")] bool loud = false,
        [Arg("Colour", Choices = new[] { "red", "green" })] string color = "red")
    {
        Calls.Enqueue($"echo {text}");
        var said = loud ? text.ToUpperInvariant() : text;
        return new Outcome($"said {string.Join(" ", Enumerable.Repeat(said, times))} in {color}");
    }

    [Command("wipe", "Delete the demo data.", Destructive = true, Local = true)]
    public Outcome Wipe([Arg("What to wipe", Positional = true)] string what)
    {
        Calls.Enqueue($"wipe {what}");
        return new Outcome($"wiped {what}");
    }

    [Command("ask", "Ask for a password the way a login to a server does.", Local = true)]
    public Outcome Ask()
    {
        var answer = Context.Session.PromptPassword?.Invoke("demo@host");
        return new Outcome(answer == null ? "no answer" : $"got {answer.Length} characters");
    }

    /// <summary>Lets <see cref="Hang"/> finish.</summary>
    public static readonly SemaphoreSlim HangRelease = new(0);

    [Command("hang", "Wait without listening to cancel, like a server command that cannot be stopped.", Local = true)]
    public async Task<Outcome> Hang()
    {
        await HangRelease.WaitAsync();
        return new Outcome("released");
    }

    [Command("pair", "Take two optional words.", ReadOnly = true, Local = true)]
    public Outcome Pair(
        [Arg("First word", Positional = true)] string? first = null,
        [Arg("Second word", Positional = true)] string? second = null) =>
        new($"{first ?? "-"} {second ?? "-"}");

    [Command("status", "Report how things are.", ReadOnly = true, Local = true)]
    public object Status()
    {
        Calls.Enqueue("status");
        return new { state = "fine", items = 3 };
    }

    [Command("stream", "Print a line, then wait until cancelled.", Local = true, Streams = true)]
    public async Task<Outcome> Stream([Arg("Line to print", Positional = true)] string line, CancellationToken ct = default)
    {
        Lines(line);
        await Task.Delay(System.Threading.Timeout.Infinite, ct);
        return new Outcome("never");
    }
}

/// <summary>
/// Runs lrw interactive without a terminal: Terminal.Gui's ANSI driver on a virtual screen, keys injected one per
/// main loop iteration, and waits for text to appear on the screen before going on.
/// </summary>
internal sealed class InteractiveScript
{
    private const int Width = 110;
    private const int Height = 34;
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(20);

    private readonly List<object> _steps = [];

    public static CommandCatalog Demo { get; } = CommandCatalog.Build(typeof(DemoCommands).Assembly);

    public CommandCatalog Catalog { get; init; } = Demo;

    public InteractiveOptions Options { get; init; } = new()
    {
        Resolve = _ => new ConnectionTarget("203.0.113.10", "root", "/keys/id", null, "demo-server"),
        LoadProfiles = () => [],
        DefaultProfile = () => null,
        Windows = false
    };

    /// <summary>Every screen the script waited for, in order.</summary>
    public List<string> Screens { get; } = [];

    /// <summary>What lrw interactive returned, once <see cref="Run"/> is done.</summary>
    public int ExitCode { get; private set; }

    // The virtual screen keeps the size the test gives it. By default the driver polls the real console for its size
    // (Console.WindowWidth/Height) and takes that over: a CI runner has a console (here 30 rows) where a local test
    // run has none. Asked over ANSI instead, nobody answers in a test, so the size stays the test's.
    static InteractiveScript() => Driver.SizeDetection = SizeDetectionMode.AnsiQuery;

    public InteractiveScript Press(Key key, int times = 1)
    {
        // A fresh key each time: a handler marks the key it got as handled, and a handled key is not pressed again
        for (var i = 0; i < times; i++) _steps.Add(new Key(key));
        return this;
    }

    public InteractiveScript Type(string text)
    {
        foreach (var c in text) _steps.Add(new Key(c));
        return this;
    }

    /// <summary>The terminal window changes size, as when the user drags its edge.</summary>
    public InteractiveScript Resize(int width, int height)
    {
        _steps.Add(new Size(width, height));
        return this;
    }

    /// <summary>Presses the button with this text on the window (what a mouse click on it does).</summary>
    public InteractiveScript Click(string button)
    {
        _steps.Add(new ButtonPress(button));
        return this;
    }

    /// <summary>Looks at the application between two steps (what has the focus, for example).</summary>
    public InteractiveScript Check(Action<IApplication> check)
    {
        _steps.Add(check);
        return this;
    }

    /// <summary>Waits until the condition holds (a dialog has closed, for example).</summary>
    public InteractiveScript WaitUntil(string what, Func<IApplication, bool> condition)
    {
        _steps.Add(new Until(what, condition));
        return this;
    }

    /// <summary>Waits until only the main window is open: whatever dialog was up has closed.</summary>
    public InteractiveScript WaitForMainWindow() =>
        WaitUntil("the main window, with no dialog over it", app => app.TopRunnableView is MainWindow);

    /// <summary>Waits until the screen shows the text.</summary>
    public InteractiveScript WaitFor(string text)
    {
        _steps.Add(new Wait(text));
        return this;
    }

    /// <summary>Runs the script, then leaves lrw with Esc presses; returns the last screen.</summary>
    public string Run()
    {
        VirtualTimeProvider time = new();
        using var app = Application.Create(time);
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(Width, Height);

        using var shell = new InteractiveApp(app, Catalog, Options);
        var steps = new Queue<object>(_steps);
        var started = DateTime.UtcNow;
        string? failure = null;

        app.Iteration += (_, _) =>
        {
            // Timers (the run window's clock) follow the virtual time
            time.Advance(TimeSpan.FromMilliseconds(50));
            if (DateTime.UtcNow - started > GiveUpAfter * 2)
            {
                // Something does not close with Esc (a dialog that keeps asking): give up on the whole run
                app.LayoutAndDraw();
                throw new TimeoutException((failure ?? "lrw interactive did not close") + "\n--- screen now ---\n" + app.Driver.ToString());
            }

            if (steps.Count == 0 || failure != null)
            {
                app.InjectKey(Key.Esc); // close whatever is open, down to leaving lrw
                return;
            }

            if (DateTime.UtcNow - started > GiveUpAfter)
            {
                app.LayoutAndDraw();
                failure = $"Gave up waiting for: {steps.Peek()}\n--- screen then ---\n{app.Driver.ToString()}";
                return;
            }

            switch (steps.Peek())
            {
                case Key key:
                    steps.Dequeue();
                    app.InjectKey(key);
                    break;
                case Size size:
                    steps.Dequeue();
                    app.Driver.SetScreenSize(size.Width, size.Height);
                    break;
                case Until until:
                    if (!until.Condition(app)) return;
                    steps.Dequeue();
                    break;
                case ButtonPress press:
                    if (FindButton(app.TopRunnableView, press.Text) is not { } button) return; // not on screen yet
                    steps.Dequeue();
                    button.InvokeCommand(Command.Accept);
                    break;
                case Action<IApplication> check:
                    steps.Dequeue();
                    try
                    {
                        check(app);
                    }
                    catch (Exception ex)
                    {
                        failure = $"Check failed: {ex.Message}";
                    }
                    break;
                case Wait wait:
                    app.LayoutAndDraw();
                    var screen = app.Driver.ToString() ?? "";
                    if (!screen.Contains(wait.Text, StringComparison.Ordinal)) return;
                    Screens.Add(screen);
                    steps.Dequeue();
                    break;
            }
        };

        ExitCode = shell.Run();
        app.LayoutAndDraw();
        var last = app.Driver.ToString() ?? "";
        if (failure != null)
            throw new Xunit.Sdk.XunitException($"{failure}\n--- last screen ---\n{last}");
        return last;
    }

    private static Button? FindButton(View? view, string text)
    {
        if (view == null || !view.Visible) return null;
        if (view is Button button && button.Text.Replace("_", "") == text) return button;
        foreach (var sub in view.SubViews)
            if (FindButton(sub, text) is { } found) return found;
        return null;
    }

    private sealed record Size(int Width, int Height);

    private sealed record Until(string What, Func<IApplication, bool> Condition)
    {
        public override string ToString() => What;
    }

    private sealed record ButtonPress(string Text)
    {
        public override string ToString() => $"the button {Text}";
    }

    private sealed record Wait(string Text)
    {
        public override string ToString() => $"\"{Text}\" on the screen";
    }
}

/// <summary>
/// The server for tests of the module screens: answers the commands it was told about, keeps every call, and runs
/// local commands for real. A server command nobody answered fails at once instead of dialling out.
/// </summary>
internal sealed class FakeServer
{
    private readonly Dictionary<string, Func<IReadOnlyDictionary<string, object?>, InvocationResult>> _answers = new();

    public ConcurrentQueue<(string Command, IReadOnlyDictionary<string, object?> Values)> Calls { get; } = new();

    public FakeServer Answer(string command, object? data, string message = "Done.") =>
        Answer(command, _ => Ok(command, data, message));

    public FakeServer Answer(string command, Func<IReadOnlyDictionary<string, object?>, InvocationResult> answer)
    {
        _answers[command] = answer;
        return this;
    }

    public IEnumerable<IReadOnlyDictionary<string, object?>> CallsOf(string command) =>
        Calls.Where(c => c.Command == command).Select(c => c.Values);

    public static InvocationResult Ok(string command, object? data, string message = "Done.") =>
        new(new JsonObject
        {
            ["ok"] = true,
            ["command"] = command,
            ["message"] = message,
            ["data"] = data == null ? null : JsonSerializer.SerializeToNode(data, JsonOutput.Options)
        }, ExitCodes.Ok);

    public static InvocationResult Fail(string command, string message) =>
        new(new JsonObject
        {
            ["ok"] = false,
            ["command"] = command,
            ["error"] = new JsonObject { ["kind"] = "failed", ["message"] = message }
        }, ExitCodes.Failed);

    public Task<InvocationResult> Invoke(CommandSpec command, CommandContext context, IReadOnlyDictionary<string, object?> values,
        CancellationToken ct)
    {
        Calls.Enqueue((command.FullPath, new Dictionary<string, object?>(values)));
        if (_answers.TryGetValue(command.FullPath, out var answer)) return Task.FromResult(answer(values));
        return command.Attribute.Local
            ? CommandInvoker.InvokeAsync(command, context, values, ct)
            : Task.FromResult(Fail(command.FullPath, $"The test server does not answer {command.FullPath}."));
    }
}

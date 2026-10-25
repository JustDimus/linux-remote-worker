using System.Text;
using System.Text.Json.Nodes;
using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Cli.Mcp;
using LinuxRemoteWorker.Cli.Session;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        AppLog.Source = "cli";

        var catalog = CommandCatalog.Build(typeof(Program).Assembly);
        var json = (CommandLineParser.PeekFormat(args) ?? DefaultFormat()) == OutputFormat.Json;

        ParsedCommandLine parsed;
        try
        {
            parsed = CommandLineParser.Parse(catalog, args);
        }
        catch (UsageException ex)
        {
            return UsageError(ex.Message, ex.HelpPath, json);
        }

        var format = parsed.Globals.Format ?? DefaultFormat();
        json = format == OutputFormat.Json;

        switch (parsed.Builtin)
        {
            case "version":
                Console.WriteLine(json ? new JsonObject { ["version"] = HelpWriter.Version }.ToJsonString(JsonOutput.Options) : HelpWriter.Version);
                return ExitCodes.Ok;
            case "schema":
                Console.WriteLine(HelpWriter.Schema(catalog, parsed.Words.FirstOrDefault()).ToJsonString(JsonOutput.Options));
                return ExitCodes.Ok;
            case "help":
                return Help(catalog, parsed.Words);
            case "mcp":
                AppLog.Source = "mcp";
                AppLog.CleanupOldFiles();
                return await McpServer.RunAsync(catalog, parsed.Globals.Connection);
        }

        if (parsed.Command == null)
        {
            if (parsed.Words.Count == 0 || parsed.Globals.Help) return Help(catalog, parsed.Words);
            var prefixKnown = HelpWriter.WriteGroup(catalog, parsed.Words, Console.Out);
            return prefixKnown ? ExitCodes.Ok : UsageError($"Unknown command 'lrw {string.Join(' ', parsed.Words)}'.", parsed.Words[0], json);
        }

        if (parsed.Globals.Help)
        {
            HelpWriter.WriteCommand(parsed.Command, Console.Out);
            return ExitCodes.Ok;
        }

        AppLog.CleanupOldFiles();
        AppLog.Info($"---- lrw {string.Join(' ', parsed.Redacted)} ----");
        return await RunAsync(parsed, parsed.Command, json);
    }

    private static async Task<int> RunAsync(ParsedCommandLine parsed, CommandSpec command, bool json)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            if (cts.IsCancellationRequested) return; // second Ctrl+C: let the process die
            e.Cancel = true;
            cts.Cancel();
            // Most SSH commands cannot be interrupted; do not leave the user waiting for them
            _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => Environment.Exit(ExitCodes.Cancelled));
        };

        var quiet = parsed.Globals.Quiet;
        using var session = new ServerSession(() => TargetResolver.Resolve(parsed.Globals.Connection))
        {
            PromptPassphrase = Console.IsInputRedirected ? null : PromptPassphrase
        };
        var context = new CommandContext(session, line => { if (!quiet) Console.Error.WriteLine(line); }, progressVisible: !quiet);

        var result = await CommandInvoker.InvokeAsync(command, context, parsed.Values, cts.Token);

        if (json)
            Console.WriteLine(result.Envelope.ToJsonString(JsonOutput.Options));
        else
            TextRenderer.Write(result.Envelope, Console.Out, Console.Error, outputShown: !quiet && context.ProgressLineCount > 0);

        return result.ExitCode;
    }

    private static int Help(CommandCatalog catalog, IReadOnlyList<string> words)
    {
        if (words.Count == 0)
        {
            HelpWriter.WriteRoot(catalog, Console.Out);
            return ExitCodes.Ok;
        }

        var (command, consumed) = CommandLineParser.Resolve(catalog, words);
        if (command != null && consumed == words.Count)
        {
            HelpWriter.WriteCommand(command, Console.Out);
            return ExitCodes.Ok;
        }

        if (HelpWriter.WriteGroup(catalog, words, Console.Out)) return ExitCodes.Ok;

        Console.Error.WriteLine($"error: no command or group 'lrw {string.Join(' ', words)}'.");
        HelpWriter.WriteRoot(catalog, Console.Error);
        return ExitCodes.Invalid;
    }

    private static int UsageError(string message, string? helpPath, bool json)
    {
        var hint = $"lrw help {helpPath}".TrimEnd();
        AppLog.Warn($"lrw usage error: {message}");
        if (json)
        {
            Console.WriteLine(new JsonObject
            {
                ["ok"] = false,
                ["error"] = new JsonObject { ["kind"] = "invalid", ["message"] = message, ["hint"] = hint }
            }.ToJsonString(JsonOutput.Options));
        }
        else
        {
            Console.Error.WriteLine($"error: {message}");
            Console.Error.WriteLine($"  see: {hint}");
        }
        return ExitCodes.Invalid;
    }

    // Agents capture stdout through a pipe and get JSON; a person at a terminal gets tables
    private static OutputFormat DefaultFormat() => Console.IsOutputRedirected ? OutputFormat.Json : OutputFormat.Text;

    private static string? PromptPassphrase()
    {
        Console.Error.Write("Key passphrase: ");
        var typed = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (typed.Length > 0) typed.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) typed.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return typed.ToString();
    }
}

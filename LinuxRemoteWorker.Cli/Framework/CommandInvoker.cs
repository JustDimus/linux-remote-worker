using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using LinuxRemoteWorker.Core;
using Renci.SshNet.Common;

namespace LinuxRemoteWorker.Cli.Framework;

/// <summary>A finished command: the JSON envelope to print or return, and the process exit code.</summary>
public sealed record InvocationResult(JsonObject Envelope, int ExitCode)
{
    public bool Ok => Envelope["ok"]?.GetValue<bool>() == true;
}

/// <summary>
/// Runs a command with already-converted argument values and wraps whatever happens into the envelope
/// both the CLI and MCP return:
/// <code>
/// { "ok": true,  "command": "...", "server": "...", "message": "...", "warning": "...", "data": ..., "output": "..." }
/// { "ok": false, "command": "...", "server": "...", "error": { "kind", "message", "hint", "detail", "output" } }
/// </code>
/// </summary>
public static class CommandInvoker
{
    public static async Task<InvocationResult> InvokeAsync(
        CommandSpec spec, CommandContext context, IReadOnlyDictionary<string, object?> values, CancellationToken ct)
    {
        try
        {
            if (spec.Destructive && values.GetValueOrDefault(CommandCatalog.ConfirmName) is not true)
                throw OperationException.Refused(
                    $"'{spec.FullPath}' is destructive: {spec.Description.TrimEnd('.')}. " +
                    "Ask the user first, then repeat the call with --yes (MCP: confirm=true).");

            var target = Activator.CreateInstance(spec.Method.DeclaringType!, context)!;
            var defaults = spec.Parameters.ToDictionary(p => p.Name, p => p.Default);
            var args = spec.Method.GetParameters()
                .Select(p => p.ParameterType == typeof(CancellationToken) ? ct
                    : values.TryGetValue(p.Name!, out var v) ? v
                    : defaults.GetValueOrDefault(p.Name!))
                .ToArray();

            var returned = spec.Method.Invoke(target, BindingFlags.DoNotWrapExceptions, null, args, null);
            var result = await UnwrapAsync(spec.Method, returned);
            return Success(spec, context, result);
        }
        catch (Exception ex)
        {
            return Failure(spec, context, ex);
        }
    }

    private static async Task<object?> UnwrapAsync(MethodInfo method, object? returned)
    {
        if (returned is not Task task) return returned;
        await task;
        // An "async Task" method's runtime type is Task<VoidTaskResult>; only declared Task<T> has a result
        return method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)
            ? method.ReturnType.GetProperty(nameof(Task<object>.Result))!.GetValue(task)
            : null;
    }

    private static InvocationResult Success(CommandSpec spec, CommandContext context, object? result)
    {
        var envelope = Header(spec, context, ok: true);
        var exitCode = ExitCodes.Ok;

        if (result is Outcome outcome)
        {
            envelope["message"] = outcome.Message;
            if (outcome.Warning != null) envelope["warning"] = outcome.Warning;
            if (outcome.Data != null) envelope["data"] = ToNode(outcome.Data);
            if (outcome.Output != null) envelope["output"] = outcome.Output;
            exitCode = outcome.ExitCode;
        }
        else if (result != null)
        {
            envelope["data"] = ToNode(result);
        }

        AttachUnseenOutput(envelope, context);
        return new InvocationResult(envelope, exitCode);
    }

    private static InvocationResult Failure(CommandSpec spec, CommandContext context, Exception ex)
    {
        var (kind, exitCode, message) = Classify(ex);
        var error = new JsonObject { ["kind"] = kind, ["message"] = message };

        switch (ex)
        {
            case ConnectionFailedException cf:
                error["target"] = cf.Target;
                error["detail"] = cf.Problem.Detail;
                error["hint"] = cf.Problem.Hint;
                break;
            case UsageException usage:
                error["hint"] = $"lrw help {usage.HelpPath ?? spec.FullPath}";
                break;
            case OperationException { Output: { } output }:
                error["output"] = output;
                break;
        }

        if (exitCode == ExitCodes.Failed && kind == "error")
            AppLog.Error($"lrw {spec.FullPath} failed", ex);
        else
            AppLog.Warn($"lrw {spec.FullPath}: {kind}: {message}");

        var envelope = Header(spec, context, ok: false);
        envelope["error"] = error;
        if (error["output"] == null) AttachUnseenOutput(error, context);
        return new InvocationResult(envelope, exitCode);
    }

    private static (string Kind, int ExitCode, string Message) Classify(Exception ex) => ex switch
    {
        OperationException op => (KindName(op.Kind), ExitCodes.For(op.Kind), op.Message),
        UsageException => ("invalid", ExitCodes.Invalid, ex.Message),
        ConnectionFailedException => ("connection", ExitCodes.Connection, ex.Message),
        OperationCanceledException => ("cancelled", ExitCodes.Cancelled, "Cancelled."),
        SshOperationTimeoutException => ("failed", ExitCodes.Failed, $"Timed out: {ex.Message}"),
        SftpPathNotFoundException => ("not-found", ExitCodes.NotFound, ex.Message),
        SftpPermissionDeniedException => ("failed", ExitCodes.Failed, $"Permission denied on the server: {ex.Message}"),
        SshConnectionException or SocketException or ProxyException =>
            ("connection", ExitCodes.Connection, $"The SSH connection was lost: {ex.Message}"),
        InvalidOperationException { Message: "Not connected" } =>
            ("connection", ExitCodes.Connection, "The SSH connection was lost."),
        FileNotFoundException or DirectoryNotFoundException => ("not-found", ExitCodes.NotFound, ex.Message),
        ArgumentException => ("invalid", ExitCodes.Invalid, ex.Message),
        UnauthorizedAccessException => ("failed", ExitCodes.Failed, ex.Message),
        _ => ("error", ExitCodes.Failed, ex.Message)
    };

    private static string KindName(FailureKind kind) => kind switch
    {
        FailureKind.Invalid => "invalid",
        FailureKind.Refused => "refused",
        FailureKind.NotFound => "not-found",
        _ => "failed"
    };

    private static JsonObject Header(CommandSpec spec, CommandContext context, bool ok)
    {
        var envelope = new JsonObject { ["ok"] = ok, ["command"] = spec.FullPath };
        if (context.Session.Connected is { } server) envelope["server"] = server.Display;
        return envelope;
    }

    // An MCP client cannot watch the live output, so the result carries its tail
    private static void AttachUnseenOutput(JsonObject target, CommandContext context)
    {
        if (!context.ProgressVisible && context.ProgressLineCount > 0 && target["output"] == null)
            target["output"] = string.Join("\n", context.RecentLines);
    }

    public static JsonNode? ToNode(object value) =>
        value as JsonNode ?? JsonSerializer.SerializeToNode(value, value.GetType(), JsonOutput.Options);
}

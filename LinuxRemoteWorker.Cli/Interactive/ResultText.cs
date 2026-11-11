using System.Text.Json.Nodes;
using LinuxRemoteWorker.Cli.Framework;

namespace LinuxRemoteWorker.Cli.Interactive;

/// <summary>What a finished command has to say, the way the command line says it.</summary>
internal static class ResultText
{
    /// <summary>The whole result as the command line prints it (data, warning, error with hint), message last.</summary>
    public static string Render(InvocationResult result, bool outputShown = false)
    {
        var envelope = (JsonObject)result.Envelope.DeepClone();
        var message = result.Ok ? Text(envelope["message"]) : null;
        envelope.Remove("message");

        var writer = new StringWriter();
        TextRenderer.Write(envelope, writer, writer, outputShown);
        if (message != null) writer.WriteLine(message);
        return string.Join("\n", writer.ToString().TrimEnd().Replace("\r\n", "\n").Split('\n').Select(OutputText.Clean));
    }

    /// <summary>One line for a status line: the message, or what went wrong.</summary>
    public static string Summary(InvocationResult result) =>
        result.Ok
            ? Text(result.Envelope["message"]) ?? "Done."
            : Text(result.Envelope["error"]?["message"]) ?? "Failed.";

    /// <summary>A failure that happened before the command could run.</summary>
    public static InvocationResult Failure(string command, string message) =>
        new(new JsonObject
        {
            ["ok"] = false,
            ["command"] = command,
            ["error"] = new JsonObject { ["kind"] = "invalid", ["message"] = message }
        }, ExitCodes.Invalid);

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Cli.Session;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Cli.Mcp;

/// <summary>
/// A Model Context Protocol server on stdio (newline-delimited JSON-RPC 2.0). Every catalog command
/// offered on <see cref="Surface.Mcp"/> becomes a tool with the same name as its CLI path
/// ("nginx sites create" → nginx_sites_create) and returns the same JSON envelope as `lrw --json`.
/// Implements initialize, ping, tools/list, tools/call, progress and cancellation notifications.
/// </summary>
public sealed class McpServer
{
    private const string LatestProtocol = "2025-06-18";
    private static readonly string[] SupportedProtocols = ["2025-06-18", "2025-03-26", "2024-11-05"];

    private const string Instructions =
        "Linux Remote Worker manages Linux servers over SSH: system info, PostgreSQL, git repositories, .NET apps run " +
        "by systemd, nginx sites with Let's Encrypt certificates, the ufw firewall, and server/app logs. " +
        "Every tool returns a JSON envelope {ok, command, server, message, warning, data, output} or " +
        "{ok:false, error:{kind, message, hint, detail, output}}; `server` names the machine that was touched. " +
        "Start with connection_test (profiles_list shows saved servers, connection_use switches to another). " +
        "Destructive tools need confirm=true - set it only after the user explicitly agreed to that exact change. " +
        "Prefer the dedicated tools over system_exec and pg_sql: they validate input and roll back failed nginx and " +
        "PostgreSQL changes. Installs, builds and certificate requests can take several minutes.";

    private readonly ServerSession _session;
    private readonly Dictionary<string, CommandSpec> _tools;
    private readonly SemaphoreSlim _toolGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    private readonly object _writeLock = new();
    private TextWriter _out = TextWriter.Null;

    private McpServer(CommandCatalog catalog, ServerSession session)
    {
        _session = session;
        _tools = catalog.For(Surface.Mcp).ToDictionary(c => c.ToolName);
    }

    public static async Task<int> RunAsync(CommandCatalog catalog, ConnectionOptions connection)
    {
        using var session = new ServerSession(() => ResolveTarget(connection));
        return await new McpServer(catalog, session).ServeAsync();
    }

    // The CLI's "pass --profile" advice means nothing to a model; tell it which tool to call instead
    private static ConnectionTarget ResolveTarget(ConnectionOptions connection)
    {
        try
        {
            return TargetResolver.Resolve(connection);
        }
        catch (UsageException ex)
        {
            throw new UsageException(
                $"{ex.Message} In this session: call connection_use with a profile from profiles_list " +
                "(or host + key), or start the server with `lrw mcp --profile <name>`.", "connection use");
        }
    }

    private async Task<int> ServeAsync()
    {
        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        _out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        AppLog.Info($"MCP server started with {_tools.Count} tools");

        var pending = new List<Task>();
        while (await stdin.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonNode? message;
            try
            {
                message = JsonNode.Parse(line);
            }
            catch (JsonException ex)
            {
                SendError(null, -32700, $"Parse error: {ex.Message}");
                continue;
            }

            // Batches only exist in protocol 2025-03-26, but cost nothing to accept
            List<JsonObject> items = message switch
            {
                JsonArray batch => batch.OfType<JsonObject>().ToList(),
                JsonObject single => [single],
                _ => []
            };
            pending.AddRange(items.Select(HandleAsync));
            pending.RemoveAll(t => t.IsCompleted);
        }

        // stdin closed: the client is gone
        foreach (var cts in _running.Values) cts.Cancel();
        try { await Task.WhenAll(pending); } catch { /* each call already reported its own failure */ }
        AppLog.Info("MCP server stopped (client closed stdin)");
        return 0;
    }

    private async Task HandleAsync(JsonObject message)
    {
        var id = message["id"]?.DeepClone();
        var method = message["method"] is JsonValue m && m.TryGetValue<string>(out var name) ? name : null;
        if (method == null) return; // a response - this server never sends requests

        var parameters = message["params"] as JsonObject;
        try
        {
            switch (method)
            {
                case "initialize":
                    SendResult(id, Initialize(parameters));
                    break;
                case "ping":
                    SendResult(id, new JsonObject());
                    break;
                case "tools/list":
                    SendResult(id, ListTools());
                    break;
                case "tools/call":
                    if (await CallToolAsync(id, parameters) is { } result) SendResult(id, result);
                    break;
                case "notifications/cancelled":
                    if (parameters?["requestId"] is { } requestId && _running.TryGetValue(requestId.ToJsonString(), out var cts))
                        cts.Cancel();
                    break;
                default:
                    // notifications/initialized and other notifications need no answer
                    if (id != null) SendError(id, -32601, $"Method not found: {method}");
                    break;
            }
        }
        catch (McpException ex)
        {
            if (id != null) SendError(id, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            AppLog.Error($"MCP {method} failed", ex);
            if (id != null) SendError(id, -32603, ex.Message);
        }
    }

    private static JsonObject Initialize(JsonObject? parameters)
    {
        var requested = parameters?["protocolVersion"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var version = requested != null && SupportedProtocols.Contains(requested) ? requested : LatestProtocol;
        var client = parameters?["clientInfo"]?["name"]?.ToString() ?? "unknown client";
        AppLog.Info($"MCP initialize: {client}, protocol {version}");

        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "linux-remote-worker",
                ["title"] = "Linux Remote Worker",
                ["version"] = HelpWriter.Version
            },
            ["instructions"] = Instructions
        };
    }

    private JsonObject ListTools()
    {
        var tools = new JsonArray();
        foreach (var spec in _tools.Values)
        {
            tools.Add(new JsonObject
            {
                ["name"] = spec.ToolName,
                ["description"] = Describe(spec),
                ["inputSchema"] = InputSchema(spec),
                ["annotations"] = new JsonObject
                {
                    ["title"] = spec.FullPath,
                    ["readOnlyHint"] = spec.Attribute.ReadOnly,
                    ["destructiveHint"] = spec.Destructive || spec.Attribute.Unrestricted,
                    ["idempotentHint"] = spec.Attribute.ReadOnly || spec.Attribute.Idempotent,
                    ["openWorldHint"] = !spec.Attribute.Local
                }
            });
        }
        return new JsonObject { ["tools"] = tools };
    }

    private static string Describe(CommandSpec spec)
    {
        var text = new StringBuilder(spec.Description);
        if (spec.Destructive)
            text.Append(" DESTRUCTIVE: requires confirm=true, which may only be set after the user agreed to this exact change.");
        if (spec.Attribute.Unrestricted)
            text.Append(" Unrestricted: it can change anything, so review the input carefully.");
        if (spec.Attribute.Streams)
            text.Append(" Can take several minutes; the result carries the last lines of output.");
        return text.ToString();
    }

    private static JsonObject InputSchema(CommandSpec spec)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var p in spec.Parameters)
        {
            var schema = new JsonObject { ["type"] = p.TypeName, ["description"] = p.Description };
            if (p.Choices.Count > 0) schema["enum"] = new JsonArray(p.Choices.Select(c => (JsonNode?)c).ToArray());
            if (!p.Required && !p.IsConfirm && p.DefaultNode is { } d) schema["default"] = d;
            properties[p.Name] = schema;
            if (p.Required || p.IsConfirm) required.Add((JsonNode?)p.Name);
        }

        var result = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false
        };
        if (required.Count > 0) result["required"] = required;
        return result;
    }

    /// <summary>Runs a tool. Returns null when the call was cancelled (a cancelled request gets no response).</summary>
    private async Task<JsonObject?> CallToolAsync(JsonNode? id, JsonObject? parameters)
    {
        var name = parameters?["name"]?.ToString() ?? throw new McpException(-32602, "tools/call needs a tool name.");
        if (!_tools.TryGetValue(name, out var spec)) throw new McpException(-32602, $"Unknown tool: {name}");

        var arguments = parameters!["arguments"] as JsonObject ?? new JsonObject();
        var progressToken = parameters["_meta"]?["progressToken"]?.DeepClone();

        Dictionary<string, object?> values;
        try
        {
            values = Bind(spec, arguments);
        }
        catch (UsageException ex)
        {
            return ToolResult(ErrorEnvelope(spec, ex.Message), isError: true);
        }

        using var cts = new CancellationTokenSource();
        var key = id?.ToJsonString();
        if (key != null) _running[key] = cts;
        try
        {
            await _toolGate.WaitAsync(cts.Token);
            try
            {
                AppLog.Info($"MCP tool {name} {Redacted(spec, arguments)}");
                var progress = 0;
                var context = new CommandContext(_session,
                    line => { if (progressToken != null) Notify(progressToken, ++progress, line); },
                    progressVisible: false);

                var result = await CommandInvoker.InvokeAsync(spec, context, values, cts.Token);
                return cts.IsCancellationRequested ? null : ToolResult(result.Envelope, isError: !result.Ok);
            }
            finally
            {
                _toolGate.Release();
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            if (key != null) _running.TryRemove(key, out _);
        }
    }

    private static Dictionary<string, object?> Bind(CommandSpec spec, JsonObject arguments)
    {
        var known = spec.Parameters.Select(p => p.Name).ToHashSet();
        var unknown = arguments.Select(a => a.Key).Where(k => !known.Contains(k)).ToList();
        if (unknown.Count > 0)
            throw new UsageException($"Unknown argument(s): {string.Join(", ", unknown)}. Expected: {string.Join(", ", known)}.");

        var values = new Dictionary<string, object?>();
        foreach (var p in spec.Parameters)
        {
            if (arguments.TryGetPropertyValue(p.Name, out var node) && node != null)
                values[p.Name] = ValueConverter.FromJson(p, node);
            else if (p.Required)
                throw new UsageException($"'{p.Name}' is required: {p.Description}");
        }
        return values;
    }

    private static JsonObject ToolResult(JsonObject envelope, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject
        {
            ["type"] = "text",
            ["text"] = envelope.ToJsonString(JsonOutput.Options)
        }),
        ["isError"] = isError
    };

    private static JsonObject ErrorEnvelope(CommandSpec spec, string message) => new()
    {
        ["ok"] = false,
        ["command"] = spec.FullPath,
        ["error"] = new JsonObject { ["kind"] = "invalid", ["message"] = message }
    };

    private static string Redacted(CommandSpec spec, JsonObject arguments)
    {
        var copy = (JsonObject)arguments.DeepClone();
        foreach (var p in spec.Parameters.Where(p => p.Secret && copy.ContainsKey(p.Name)))
            copy[p.Name] = "***";
        return copy.ToJsonString(JsonOutput.Compact);
    }

    // ── Wire ───────────────────────────────────────────────────────────────

    private void Notify(JsonNode progressToken, int progress, string message) => Send(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["method"] = "notifications/progress",
        ["params"] = new JsonObject
        {
            ["progressToken"] = progressToken.DeepClone(),
            ["progress"] = progress,
            ["message"] = message
        }
    });

    private void SendResult(JsonNode? id, JsonObject result) => Send(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result
    });

    private void SendError(JsonNode? id, int code, string message) => Send(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    });

    // Written synchronously under a lock, so messages never interleave and progress stays in order
    private void Send(JsonObject message)
    {
        var json = message.ToJsonString(JsonOutput.Compact);
        lock (_writeLock) _out.WriteLine(json);
    }

    private sealed class McpException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}

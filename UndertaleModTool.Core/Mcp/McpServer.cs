using System.Text.Json;
using System.Text.Json.Nodes;

namespace UndertaleModTool.Core.Mcp;

/// <summary>Result of a tool call: MCP content items, and whether it's an error.</summary>
public sealed class ToolResult
{
    public JsonArray Content { get; } = new();
    public bool IsError { get; init; }

    public static ToolResult Text(string text, bool isError = false)
    {
        ToolResult result = new() { IsError = isError };
        result.AddText(text);
        return result;
    }

    public static ToolResult Json(JsonNode node) => Text(node?.ToJsonString(McpServer.PrettyJson) ?? "null");

    public static ToolResult Error(string message) => Text(message, isError: true);

    public ToolResult AddText(string text)
    {
        Content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        return this;
    }

    public ToolResult AddImage(byte[] png)
    {
        Content.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" });
        return this;
    }
}

/// <summary>A tool: name, description, JSON schema of its arguments and the handler.</summary>
public sealed record McpTool(string Name, string Description, JsonObject InputSchema, Func<JsonObject, ToolResult> Handler,
                             bool ReadOnly = false);

/// <summary>
/// A Model Context Protocol server (JSON-RPC 2.0 messages; tools only).
/// Transport-independent: <see cref="McpHttpServer"/> exposes it over Streamable HTTP.
/// </summary>
public sealed class McpServer
{
    public static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    /// <summary>Protocol versions this server can speak, newest first.</summary>
    public static readonly string[] SupportedVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };

    private readonly Dictionary<string, McpTool> _tools = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _callLock = new(1, 1);

    public string Name { get; init; } = "undertalemodtool-android";
    public string Version { get; init; } = "1.0.0";
    public string Instructions { get; init; }

    /// <summary>Raised for each handled request (method and short detail), for logging.</summary>
    public event Action<string> Log;

    public IReadOnlyCollection<McpTool> Tools => _tools.Values;

    public void Add(McpTool tool) => _tools[tool.Name] = tool;

    /// <summary>
    /// Handles one JSON-RPC message or a batch. Returns the response, or null if nothing should be
    /// sent back (notifications/responses only).
    /// </summary>
    public async Task<JsonNode> HandleAsync(JsonNode message)
    {
        if (message is JsonArray batch)
        {
            JsonArray responses = new();
            foreach (JsonNode item in batch)
            {
                JsonNode response = await HandleAsync(item).ConfigureAwait(false);
                if (response is not null)
                    responses.Add(response);
            }
            return responses.Count > 0 ? responses : null;
        }

        if (message is not JsonObject request)
            return Error(null, -32600, "Invalid request");

        JsonNode id = request["id"]?.DeepClone();
        string method = request["method"]?.GetValue<string>();
        if (method is null)
            return null; // a response from the client; nothing to do

        bool isNotification = !request.ContainsKey("id");
        JsonObject parameters = request["params"] as JsonObject ?? new JsonObject();
        try
        {
            JsonNode result = method switch
            {
                "initialize" => Initialize(parameters),
                "ping" => new JsonObject(),
                "tools/list" => ListTools(),
                "tools/call" => await CallToolAsync(parameters).ConfigureAwait(false),
                "resources/list" => new JsonObject { ["resources"] = new JsonArray() },
                "resources/templates/list" => new JsonObject { ["resourceTemplates"] = new JsonArray() },
                "prompts/list" => new JsonObject { ["prompts"] = new JsonArray() },
                "logging/setLevel" => new JsonObject(),
                _ when method.StartsWith("notifications/", StringComparison.Ordinal) => null,
                _ => throw new McpException(-32601, $"Method not found: {method}"),
            };
            if (isNotification)
                return null;
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result ?? new JsonObject() };
        }
        catch (McpException e)
        {
            return isNotification ? null : Error(id, e.Code, e.Message);
        }
        catch (Exception e)
        {
            return isNotification ? null : Error(id, -32603, e.Message);
        }
    }

    private static JsonObject Error(JsonNode id, int code, string message)
        => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private JsonObject Initialize(JsonObject parameters)
    {
        string requested = parameters["protocolVersion"]?.GetValue<string>();
        string version = SupportedVersions.Contains(requested) ? requested : SupportedVersions[0];
        string client = parameters["clientInfo"]?["name"]?.GetValue<string>() ?? "unknown client";
        Log?.Invoke($"initialize from {client} (protocol {requested ?? "?"})");

        JsonObject result = new()
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = Name, ["title"] = "UndertaleModTool (Android)", ["version"] = Version },
        };
        if (Instructions is not null)
            result["instructions"] = Instructions;
        return result;
    }

    private JsonObject ListTools()
    {
        JsonArray tools = new();
        foreach (McpTool tool in _tools.Values)
        {
            JsonObject entry = new()
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone(),
            };
            if (tool.ReadOnly)
                entry["annotations"] = new JsonObject { ["readOnlyHint"] = true };
            tools.Add(entry);
        }
        return new JsonObject { ["tools"] = tools };
    }

    private async Task<JsonObject> CallToolAsync(JsonObject parameters)
    {
        string name = parameters["name"]?.GetValue<string>() ?? throw new McpException(-32602, "Missing tool name");
        if (!_tools.TryGetValue(name, out McpTool tool))
            throw new McpException(-32602, $"Unknown tool: {name}");
        JsonObject arguments = parameters["arguments"] as JsonObject ?? new JsonObject();
        Log?.Invoke($"tools/call {name}");

        ToolResult result;
        // Tools mutate shared state (the loaded data), so run them one at a time.
        await _callLock.WaitAsync().ConfigureAwait(false);
        try
        {
            result = await Task.Run(() => tool.Handler(arguments)).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            result = ToolResult.Error($"{e.GetType().Name}: {e.Message}");
        }
        finally
        {
            _callLock.Release();
        }

        JsonObject response = new() { ["content"] = result.Content.DeepClone() };
        if (result.IsError)
            response["isError"] = true;
        return response;
    }
}

public sealed class McpException : Exception
{
    public int Code { get; }

    public McpException(int code, string message) : base(message) => Code = code;
}

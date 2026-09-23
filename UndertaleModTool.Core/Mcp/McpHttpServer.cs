using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UndertaleModTool.Core.Mcp;

/// <summary>
/// Serves an <see cref="McpServer"/> over MCP's "Streamable HTTP" transport (JSON responses, no SSE)
/// with a small HTTP/1.1 server, plus a web console at "/".
/// </summary>
/// <remarks>
/// Requests to /mcp must carry the access token, as "Authorization: Bearer &lt;token&gt;" (or the
/// "token" query parameter for clients that can't set headers).
/// </remarks>
public sealed class McpHttpServer : IDisposable
{
    private const int MaxBodySize = 256 * 1024 * 1024;

    private readonly McpServer _mcp;
    private TcpListener _listener;
    private CancellationTokenSource _cts;
    private readonly HashSet<string> _sessions = new();

    public McpHttpServer(McpServer mcp) => _mcp = mcp;

    public int Port { get; private set; }
    public bool AllowRemote { get; private set; }
    public string Token { get; set; } = CreateToken();
    public bool IsRunning => _listener is not null;

    /// <summary>HTML served at "/" (the web console).</summary>
    public string HomePage { get; set; } = "<h1>UndertaleModTool MCP server</h1>";

    public event Action<string> Log;

    public static string CreateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>Starts listening on 127.0.0.1 (or all interfaces if <paramref name="allowRemote"/>).</summary>
    public void Start(int port, bool allowRemote)
    {
        Stop();
        _listener = new TcpListener(allowRemote ? IPAddress.Any : IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        AllowRemote = allowRemote;
        _cts = new CancellationTokenSource();
        _ = AcceptLoop(_listener, _cts.Token);
        Log?.Invoke($"Listening on {(allowRemote ? "0.0.0.0" : "127.0.0.1")}:{Port}");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
        _cts = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                return;
            }
            _ = Task.Run(() => HandleConnection(client, ct), ct);
        }
    }

    private sealed class Request
    {
        public string Method, Path, Query;
        public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = Array.Empty<byte>();
    }

    private async Task HandleConnection(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            NetworkStream stream = client.GetStream();
            BufferedReader reader = new(stream);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    Request request = await ReadRequest(reader, ct).ConfigureAwait(false);
                    if (request is null)
                        return;
                    bool keepAlive = !string.Equals(request.Headers.GetValueOrDefault("Connection"), "close", StringComparison.OrdinalIgnoreCase);
                    await Respond(stream, request, keepAlive).ConfigureAwait(false);
                    if (!keepAlive)
                        return;
                }
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // Connection closed.
            }
            catch (InvalidDataException e)
            {
                try
                {
                    await WriteResponse(stream, 400, "text/plain", Encoding.UTF8.GetBytes(e.Message), null, false).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore
                }
            }
        }
    }

    private static async Task<Request> ReadRequest(BufferedReader reader, CancellationToken ct)
    {
        string requestLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(requestLine))
            return null;
        string[] parts = requestLine.Split(' ');
        if (parts.Length < 2)
            throw new InvalidDataException("Bad request line");

        Request request = new() { Method = parts[0].ToUpperInvariant() };
        string target = parts[1];
        int q = target.IndexOf('?');
        request.Path = q >= 0 ? target[..q] : target;
        request.Query = q >= 0 ? target[(q + 1)..] : "";

        while (true)
        {
            string line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
                return null;
            if (line.Length == 0)
                break;
            int colon = line.IndexOf(':');
            if (colon > 0)
                request.Headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            if (request.Headers.Count > 100)
                throw new InvalidDataException("Too many headers");
        }

        if (request.Headers.TryGetValue("Content-Length", out string lengthText))
        {
            if (!int.TryParse(lengthText, out int length) || length < 0 || length > MaxBodySize)
                throw new InvalidDataException("Bad Content-Length");
            request.Body = await reader.ReadBytesAsync(length, ct).ConfigureAwait(false);
        }
        else if (string.Equals(request.Headers.GetValueOrDefault("Transfer-Encoding"), "chunked", StringComparison.OrdinalIgnoreCase))
        {
            using MemoryStream body = new();
            while (true)
            {
                string sizeLine = await reader.ReadLineAsync(ct).ConfigureAwait(false) ?? throw new InvalidDataException("Truncated chunk");
                int size = Convert.ToInt32(sizeLine.Split(';')[0].Trim(), 16);
                if (size == 0)
                {
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(ct).ConfigureAwait(false)))
                    {
                    }
                    break;
                }
                if (body.Length + size > MaxBodySize)
                    throw new InvalidDataException("Body too large");
                body.Write(await reader.ReadBytesAsync(size, ct).ConfigureAwait(false));
                await reader.ReadLineAsync(ct).ConfigureAwait(false);
            }
            request.Body = body.ToArray();
        }
        return request;
    }

    private bool IsAuthorized(Request request)
    {
        if (string.IsNullOrEmpty(Token))
            return true;
        string auth = request.Headers.GetValueOrDefault("Authorization");
        if (auth is not null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && FixedEquals(auth[7..].Trim(), Token))
            return true;
        foreach (string pair in request.Query.Split('&'))
        {
            if (pair.StartsWith("token=", StringComparison.Ordinal) && FixedEquals(Uri.UnescapeDataString(pair[6..]), Token))
                return true;
        }
        return false;
    }

    private static bool FixedEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private async Task Respond(Stream stream, Request request, bool keepAlive)
    {
        Dictionary<string, string> headers = new();

        if (request.Method == "OPTIONS")
        {
            await WriteResponse(stream, 204, null, Array.Empty<byte>(), headers, keepAlive).ConfigureAwait(false);
            return;
        }

        switch (request.Path)
        {
            case "/":
            case "/index.html":
                await WriteResponse(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(HomePage), headers, keepAlive).ConfigureAwait(false);
                return;
            case "/health":
                await WriteResponse(stream, 200, "text/plain", "ok"u8.ToArray(), headers, keepAlive).ConfigureAwait(false);
                return;
            case "/mcp":
                break;
            default:
                await WriteResponse(stream, 404, "text/plain", "Not found"u8.ToArray(), headers, keepAlive).ConfigureAwait(false);
                return;
        }

        if (!IsAuthorized(request))
        {
            Log?.Invoke($"Rejected {request.Method} /mcp: missing or wrong token");
            headers["WWW-Authenticate"] = "Bearer";
            await WriteResponse(stream, 401, "application/json",
                Encoding.UTF8.GetBytes("{\"error\":\"Missing or invalid access token\"}"), headers, keepAlive).ConfigureAwait(false);
            return;
        }

        switch (request.Method)
        {
            case "GET":
                // No server-initiated stream.
                headers["Allow"] = "POST, DELETE";
                await WriteResponse(stream, 405, "text/plain", "Use POST"u8.ToArray(), headers, keepAlive).ConfigureAwait(false);
                return;
            case "DELETE":
                lock (_sessions)
                    _sessions.Remove(request.Headers.GetValueOrDefault("Mcp-Session-Id") ?? "");
                await WriteResponse(stream, 204, null, Array.Empty<byte>(), headers, keepAlive).ConfigureAwait(false);
                return;
            case "POST":
                break;
            default:
                await WriteResponse(stream, 405, "text/plain", "Method not allowed"u8.ToArray(), headers, keepAlive).ConfigureAwait(false);
                return;
        }

        JsonNode message;
        try
        {
            message = JsonNode.Parse(request.Body);
        }
        catch (JsonException e)
        {
            JsonObject error = new() { ["jsonrpc"] = "2.0", ["id"] = null, ["error"] = new JsonObject { ["code"] = -32700, ["message"] = "Parse error: " + e.Message } };
            await WriteResponse(stream, 400, "application/json", Encoding.UTF8.GetBytes(error.ToJsonString()), headers, keepAlive).ConfigureAwait(false);
            return;
        }

        if (message is JsonObject { } obj && obj["method"]?.GetValue<string>() == "initialize")
        {
            string session = Guid.NewGuid().ToString("N");
            lock (_sessions)
                _sessions.Add(session);
            headers["Mcp-Session-Id"] = session;
        }

        JsonNode response = await _mcp.HandleAsync(message).ConfigureAwait(false);
        if (response is null)
        {
            await WriteResponse(stream, 202, null, Array.Empty<byte>(), headers, keepAlive).ConfigureAwait(false);
            return;
        }
        await WriteResponse(stream, 200, "application/json", Encoding.UTF8.GetBytes(response.ToJsonString()), headers, keepAlive).ConfigureAwait(false);
    }

    private static async Task WriteResponse(Stream stream, int status, string contentType, byte[] body, Dictionary<string, string> headers, bool keepAlive)
    {
        string reason = status switch
        {
            200 => "OK",
            202 => "Accepted",
            204 => "No Content",
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            405 => "Method Not Allowed",
            _ => "Status",
        };
        StringBuilder sb = new();
        sb.Append($"HTTP/1.1 {status} {reason}\r\n");
        if (contentType is not null)
            sb.Append($"Content-Type: {contentType}\r\n");
        sb.Append($"Content-Length: {body.Length}\r\n");
        sb.Append(keepAlive ? "Connection: keep-alive\r\n" : "Connection: close\r\n");
        // Browser-based clients (and the web console) need CORS; access is still protected by the token.
        sb.Append("Access-Control-Allow-Origin: *\r\n");
        sb.Append("Access-Control-Allow-Methods: GET, POST, DELETE, OPTIONS\r\n");
        sb.Append("Access-Control-Allow-Headers: Authorization, Content-Type, Accept, Mcp-Session-Id, Mcp-Protocol-Version\r\n");
        sb.Append("Access-Control-Expose-Headers: Mcp-Session-Id\r\n");
        sb.Append("Cache-Control: no-store\r\n");
        if (headers is not null)
        {
            foreach (var (key, value) in headers)
                sb.Append($"{key}: {value}\r\n");
        }
        sb.Append("\r\n");
        byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(head).ConfigureAwait(false);
        if (body.Length > 0)
            await stream.WriteAsync(body).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>Minimal buffered reader for HTTP lines and bodies.</summary>
    private sealed class BufferedReader
    {
        private readonly Stream _stream;
        private readonly byte[] _buffer = new byte[16 * 1024];
        private int _start, _end;

        public BufferedReader(Stream stream) => _stream = stream;

        private async Task<bool> Fill(CancellationToken ct)
        {
            if (_start > 0 && _start == _end)
                _start = _end = 0;
            if (_end == _buffer.Length)
            {
                if (_start == 0)
                    throw new InvalidDataException("Header line too long");
                Array.Copy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            int read = await _stream.ReadAsync(_buffer.AsMemory(_end), ct).ConfigureAwait(false);
            if (read <= 0)
                return false;
            _end += read;
            return true;
        }

        public async Task<string> ReadLineAsync(CancellationToken ct)
        {
            while (true)
            {
                int newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
                if (newline >= 0)
                {
                    int length = newline - _start;
                    if (length > 0 && _buffer[newline - 1] == '\r')
                        length--;
                    string line = Encoding.ASCII.GetString(_buffer, _start, length);
                    _start = newline + 1;
                    return line;
                }
                if (!await Fill(ct).ConfigureAwait(false))
                    return null;
            }
        }

        public async Task<byte[]> ReadBytesAsync(int count, CancellationToken ct)
        {
            byte[] result = new byte[count];
            int copied = Math.Min(count, _end - _start);
            Array.Copy(_buffer, _start, result, 0, copied);
            _start += copied;
            while (copied < count)
            {
                int read = await _stream.ReadAsync(result.AsMemory(copied), ct).ConfigureAwait(false);
                if (read <= 0)
                    throw new IOException("Connection closed while reading body");
                copied += read;
            }
            return result;
        }
    }
}

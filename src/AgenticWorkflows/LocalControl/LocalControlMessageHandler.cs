using System.Buffers;
using System.Text.Json;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace AgenticWorkflows.LocalControl;

/// <summary>
/// Bounds the local stream and enforces the AW request/reply profile before the library dispatches.
/// StreamJsonRpc owns JSON-RPC serialization, correlation, method binding, and dispatch.
/// </summary>
internal sealed class LocalControlMessageHandler(Stream stream, bool server, int protocolVersion,
    string endpoint, CancellationToken exchangeToken)
    : MessageHandlerBase(new SystemTextJsonFormatter { JsonSerializerOptions = LocalControlProtocol.Json })
{
    private readonly TaskCompletionSource responseWritten = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool read;

    internal Task ResponseWritten => responseWritten.Task;
    public override bool CanRead => true;
    public override bool CanWrite => true;

    protected override async ValueTask<JsonRpcMessage?> ReadCoreAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, exchangeToken);
        // Never read or dispatch a second message, including pipelined requests or cancellation notifications.
        if (read)
            await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
        read = true;
        JsonRpcError? rejection;
        try
        {
            var body = await LocalControlProtocol.ReadAsync(stream, linked.Token);
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16, AllowDuplicateProperties = false });
            var root = document.RootElement;
            if (server)
            {
                ValidateRequest(root);
                // Notifications have no supported product behavior and MUST NOT receive replies.
                if (!root.TryGetProperty("id", out _))
                    return null;
            }
            else
            {
                ValidateResponse(root);
            }
            return Formatter.Deserialize(new ReadOnlySequence<byte>(body));
        }
        catch (RejectedRequest error) when (server) { rejection = error.Response; }
        catch (JsonException) when (server)
        {
            rejection = Error(RequestId.Null, JsonRpcErrorCode.ParseError, "Parse error");
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            if (!server)
                throw new JsonException("Invalid JSON-RPC message.", error);
            rejection = Error(RequestId.Null, JsonRpcErrorCode.InvalidRequest, "Invalid Request");
        }
        catch (Exception error) when (server && error is InvalidDataException or EndOfStreamException)
        {
            rejection = Error(RequestId.Null, JsonRpcErrorCode.InvalidRequest, "Invalid or incomplete frame");
        }
        await WriteAsync(rejection, linked.Token);
        return null;
    }

    private void ValidateRequest(JsonElement root)
    {
        if (!HasProtocolMarker(root) ||
            !root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String ||
            root.EnumerateObject().Any(property => property.Name is not ("jsonrpc" or "id" or "method" or "params")))
            throw new RejectedRequest(Error(RequestId.Null, JsonRpcErrorCode.InvalidRequest, "Invalid Request"));

        if (!root.TryGetProperty("id", out var id))
            return;
        var requestId = id.ValueKind switch
        {
            JsonValueKind.String => new RequestId(id.GetString()!),
            JsonValueKind.Number when id.TryGetInt64(out long number) => new RequestId(number),
            _ => throw new RejectedRequest(Error(RequestId.Null, JsonRpcErrorCode.InvalidRequest, "A string or integer id is required"))
        };
        if (!root.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("protocolVersion", out var version) || version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out int requestedVersion))
            throw new RejectedRequest(Error(requestId, JsonRpcErrorCode.InvalidParams, "params.protocolVersion must be an integer"));

        // Universal gate: runs before method lookup, parameter binding, or any application handler.
        if (requestedVersion != protocolVersion)
        {
            var diagnostic = LocalControlProtocol.Mismatch(requestedVersion, protocolVersion, endpoint);
            throw new RejectedRequest(Error(requestId, (JsonRpcErrorCode)LocalControlProtocol.ProtocolMismatch,
                diagnostic.Message, diagnostic));
        }

        // StreamJsonRpc registers extension methods during construction. None belong to the local product profile.
        if (method.GetString()!.StartsWith("$/", StringComparison.Ordinal) || method.GetString()!.StartsWith("rpc.", StringComparison.Ordinal))
            throw new RejectedRequest(Error(requestId, JsonRpcErrorCode.MethodNotFound, "Method not found"));
    }

    private static void ValidateResponse(JsonElement root)
    {
        if (!HasProtocolMarker(root) || root.TryGetProperty("method", out _) ||
            !root.TryGetProperty("id", out var id) ||
            !(id.ValueKind is JsonValueKind.String or JsonValueKind.Null ||
                id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out _)) ||
            root.TryGetProperty("result", out _) == root.TryGetProperty("error", out _))
            throw new JsonException("Expected one JSON-RPC 2.0 result or error with a request id.");
        if (root.TryGetProperty("error", out var error) &&
            (error.ValueKind != JsonValueKind.Object ||
             !error.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out _) ||
             !error.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String))
            throw new JsonException("Expected a JSON-RPC error code and message.");
    }

    private static bool HasProtocolMarker(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("jsonrpc", out var marker) &&
        marker.ValueKind == JsonValueKind.String && marker.GetString() == "2.0";

    private static JsonRpcError Error(RequestId id, JsonRpcErrorCode code, string message, object? data = null) =>
        new() { RequestId = id, Error = new() { Code = code, Message = message, Data = data } };

    protected override async ValueTask WriteCoreAsync(JsonRpcMessage content, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, exchangeToken);
        var buffer = new ArrayBufferWriter<byte>();
        Formatter.Serialize(buffer, content);
        await LocalControlProtocol.WriteAsync(stream, buffer.WrittenMemory, linked.Token);
    }

    protected override ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        // NetworkStream writes are unbuffered. Close the server exchange only after the complete reply is sent.
        if (server)
            responseWritten.TrySetResult();
        return ValueTask.CompletedTask;
    }

    private sealed class RejectedRequest(JsonRpcError response) : Exception
    {
        internal JsonRpcError Response { get; } = response;
    }
}

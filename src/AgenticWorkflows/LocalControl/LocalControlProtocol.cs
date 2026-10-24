using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticWorkflows.Application;

namespace AgenticWorkflows.LocalControl;

public sealed record ControlDiagnostic(string Code, string Message, string Guidance,
    string? Endpoint = null, int? ClientProtocolVersion = null, int? ServerProtocolVersion = null);

public sealed class LocalControlException(ControlDiagnostic diagnostic) : Exception(diagnostic.Message)
{
    public ControlDiagnostic Diagnostic { get; } = diagnostic;
}

public sealed record StatusReply(int ProtocolVersion, ControlPlaneStatus Status);

internal sealed record Request(int ProtocolVersion, string Command);
internal sealed record Response(int ProtocolVersion, ControlPlaneStatus? Status = null,
    ControlDiagnostic? Error = null);

internal static class LocalControlProtocol
{
    internal const int CurrentVersion = 1;
    internal const int MaximumFrameBytes = 16 * 1024;
    internal static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(5);
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        MaxDepth = 16,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static ControlDiagnostic Mismatch(int clientVersion, int serverVersion, string endpoint) =>
        new("protocol_mismatch",
            $"awctl protocol version {clientVersion} does not match control-plane protocol version {serverVersion}.",
            "Update awctl and the control plane together, then restart the control plane and retry awctl status.",
            endpoint, clientVersion, serverVersion);

    internal static async Task<byte[]> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 or > MaximumFrameBytes)
            throw new InvalidDataException($"JSON frames must contain 1–{MaximumFrameBytes} bytes.");
        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken);
        return body;
    }

    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (body.Length > MaximumFrameBytes)
            throw new InvalidDataException("The JSON response exceeds the frame limit.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }

    internal static T Deserialize<T>(byte[] body) =>
        JsonSerializer.Deserialize<T>(body, Json) ?? throw new JsonException("A JSON object is required.");
}

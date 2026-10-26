using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticWorkflows.Application;
using AgenticWorkflows.Infrastructure;

namespace AgenticWorkflows.LocalControl;

public sealed record ControlDiagnostic(string Code, string Message, string Guidance,
    string? Endpoint = null, int? ClientProtocolVersion = null, int? ServerProtocolVersion = null);

public sealed class LocalControlException(ControlDiagnostic diagnostic) : Exception(diagnostic.Message)
{
    public ControlDiagnostic Diagnostic { get; } = diagnostic;
}

public sealed record StatusReply(int ProtocolVersion, ControlPlaneStatus Status);
public sealed record WorkflowCommandReply(int ProtocolVersion, Guid? WorkflowId, Guid? OperationId, string Message, WorkflowSummary? Workflow, string[]? ComponentOutcomes);
public sealed record WorkflowQueryResponse(int ProtocolVersion, WorkflowQueryReply Query);
public sealed record DatabaseResponse(int ProtocolVersion, DatabaseReply Database);

internal static class LocalControlProtocol
{
    internal const int CurrentVersion = 3;
    internal const int ProtocolMismatch = -32001;
    internal const int MaximumFrameBytes = 16 * 1024;
    internal static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(5);
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        MaxDepth = 16,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    internal static ControlDiagnostic Mismatch(int clientVersion, int serverVersion, string endpoint) =>
        new("protocol_mismatch",
            $"awctl protocol version {clientVersion} does not match control-plane protocol version {serverVersion}.",
            "Update awctl and the control plane together with ./build.sh from the configured source checkout, then run awctl service restart and retry awctl status.",
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

    internal static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        if (body.Length > MaximumFrameBytes)
            throw new InvalidDataException("The JSON response exceeds the frame limit.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }
}

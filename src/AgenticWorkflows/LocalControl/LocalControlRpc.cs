using System.Text.Json;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace AgenticWorkflows.LocalControl;

internal sealed class LocalControlRpc : JsonRpc
{
    internal LocalControlRpc(IJsonRpcMessageHandler handler) : base(handler)
    {
        CancellationStrategy = null;
        CancelLocallyInvokedMethodsWhenConnectionIsClosed = false;
    }

    // AW diagnostics are data, not serialized CLR exceptions.
    protected override Type GetErrorDetailsDataType(JsonRpcError error) => typeof(JsonElement);

    protected override JsonRpcError.ErrorDetail CreateErrorDetails(JsonRpcRequest request, Exception exception) =>
        new() { Code = JsonRpcErrorCode.InternalError, Message = "The control plane could not complete the local request." };
}

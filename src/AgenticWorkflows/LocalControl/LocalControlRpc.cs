using System.Text.Json;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using AgenticWorkflows.Workflows;

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
        exception is WorkflowException workflow
        ? new() { Code = (JsonRpcErrorCode)(-32002), Message = workflow.Message,
            Data = new ControlDiagnostic(workflow.Code, workflow.Message, workflow.Guidance) }
        : new() { Code = JsonRpcErrorCode.InternalError, Message = "The control plane could not complete the local request." };
}

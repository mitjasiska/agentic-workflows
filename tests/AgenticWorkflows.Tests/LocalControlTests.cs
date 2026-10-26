using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgenticWorkflows.Application;
using AgenticWorkflows.LocalControl;
using StreamJsonRpc;

namespace AgenticWorkflows.Tests;

public sealed class LocalControlTests
{
    private const string StatusRequest = """{"jsonrpc":"2.0","id":1,"method":"status","params":{"protocolVersion":3}}""";
    private const string StatusResult = """{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":3,"status":{"state":"running","softwareVersion":"test"}}}""";

    [Fact]
    public void Application_query_runs_without_endpoint_or_transport()
    {
        var status = new ControlPlaneStatusQuery().Execute();
        Assert.Equal("running", status.State);
        Assert.StartsWith("0.1.0", status.SoftwareVersion);
    }

    [Fact]
    public async Task Status_command_returns_both_versions_over_a_real_socket()
    {
        await using var fixture = new SocketFixture();
        await fixture.StartAsync();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await AwctlCommand.RunAsync(["status"], output, error, fixture.Client()));
        using var result = JsonDocument.Parse(output.ToString());
        Assert.Equal(LocalControlProtocol.CurrentVersion, result.RootElement.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("running", result.RootElement.GetProperty("status").GetProperty("state").GetString());
        Assert.Equal("test-software-1.2.3", result.RootElement.GetProperty("status").GetProperty("softwareVersion").GetString());
        Assert.Equal("", error.ToString());
        Assert.Equal(1, fixture.Query.Calls);
    }

    [Fact]
    public async Task Standard_library_client_can_call_the_server_without_the_AW_client_adapter()
    {
        await using var fixture = new SocketFixture();
        await fixture.StartAsync();
        using var socket = await fixture.ConnectAsync();
        using var stream = new NetworkStream(socket);
        using var handler = new LengthHeaderMessageHandler(stream, stream,
            new SystemTextJsonFormatter { JsonSerializerOptions = LocalControlProtocol.Json });
        using var rpc = new JsonRpc(handler);
        rpc.StartListening();
        var reply = await rpc.InvokeWithParameterObjectAsync<StatusReply>("status", new { protocolVersion = LocalControlProtocol.CurrentVersion })
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LocalControlProtocol.CurrentVersion, reply.ProtocolVersion);
        Assert.Equal("running", reply.Status.State);
        Assert.Equal(1, fixture.Query.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unavailable_service_reports_expected_endpoint_and_start_command(bool staleSocket)
    {
        await using var fixture = new SocketFixture();
        if (staleSocket)
        {
            LinuxFileSystem.CreatePrivateDirectory(fixture.Endpoint.RuntimeDirectory);
            using var socket = LinuxFileSystem.Bind(fixture.Endpoint.SocketPath);
            File.SetUnixFileMode(fixture.Endpoint.SocketPath, LinuxFileSystem.PrivateSocket);
        }
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await AwctlCommand.RunAsync(["status"], output, error, fixture.Client()));
        var diagnostic = JsonSerializer.Deserialize<ControlDiagnostic>(error.ToString(), LocalControlProtocol.Json)!;
        AssertUnavailable(diagnostic, fixture);
        Assert.Empty(output.ToString());
        Assert.Equal(0, fixture.Query.Calls);
        Assert.Null(fixture.Server);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(2, 3)]
    public async Task Mismatch_does_not_dispatch_and_prints_versions_and_recovery(int serverVersion, int clientVersion)
    {
        await using var fixture = new SocketFixture();
        await fixture.StartAsync(serverVersion);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await AwctlCommand.RunAsync(["status"], output, error, fixture.Client(clientVersion)));
        var diagnostic = JsonSerializer.Deserialize<ControlDiagnostic>(error.ToString(), LocalControlProtocol.Json)!;
        Assert.Equal("protocol_mismatch", diagnostic.Code);
        Assert.Equal(clientVersion, diagnostic.ClientProtocolVersion);
        Assert.Equal(serverVersion, diagnostic.ServerProtocolVersion);
        Assert.Contains($"awctl protocol version {clientVersion}", diagnostic.Message);
        Assert.Contains($"control-plane protocol version {serverVersion}", diagnostic.Message);
        Assert.Contains("Update", diagnostic.Guidance);
        Assert.Contains("restart", diagnostic.Guidance);
        Assert.Empty(output.ToString());
        Assert.Equal(0, fixture.Query.Calls);
        await fixture.Client(serverVersion).StatusAsync();
        Assert.Equal(1, fixture.Query.Calls);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("futureOperation")]
    public async Task Protocol_is_checked_before_method_lookup_or_another_versions_parameter_schema(string method)
    {
        await using var fixture = new SocketFixture();
        await fixture.StartAsync();
        var response = await ExchangeAsync(fixture,
            $$$$"""{"jsonrpc":"2.0","id":"version-test","method":"{{{{method}}}}","params":{"protocolVersion":42,"futureArguments":{"different":true}}}""");
        AssertError(response, -32001);
        Assert.Equal("version-test", response.GetProperty("id").GetString());
        var data = response.GetProperty("error").GetProperty("data");
        Assert.Equal("protocol_mismatch", data.GetProperty("code").GetString());
        Assert.Equal(42, data.GetProperty("clientProtocolVersion").GetInt32());
        Assert.Equal(LocalControlProtocol.CurrentVersion, data.GetProperty("serverProtocolVersion").GetInt32());
        Assert.Equal(0, fixture.Query.Calls);
    }

    [Theory]
    [InlineData("{", -32700)]
    [InlineData("null", -32600)]
    [InlineData("[]", -32600)]
    [InlineData("{}", -32600)]
    [InlineData("""{"protocolVersion":3,"command":"status"}""", -32600)]
    [InlineData("""{"id":1,"method":"status","params":{"protocolVersion":3}}""", -32600)]
    [InlineData("""{"jsonrpc":"1.0","id":1,"method":"status","params":{"protocolVersion":3}}""", -32600)]
    [InlineData("""{"jsonrpc":"3.0","id":1,"method":"status","params":{"protocolVersion":3}}""", -32600)]
    [InlineData("""{"jsonrpc":2.0,"id":1,"method":"status","params":{"protocolVersion":3}}""", -32600)]
    [InlineData("""{"jsonrpc":null,"id":1,"method":"status","params":{"protocolVersion":3}}""", -32600)]
    [InlineData("""{"jsonrpc":"2.0","id":true,"method":"status","params":{"protocolVersion":3}}""", -32600)]
    [InlineData("""{"jsonrpc":"2.0","id":null,"method":"status","params":{"protocolVersion":3}}""", -32600)]
    [InlineData("""{"jsonrpc":"2.0","id":1.5,"method":"status","params":{"protocolVersion":3}}""", -32600)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":5,"params":{"protocolVersion":3}}""", -32600)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"\uD800","params":{"protocolVersion":3}}""", -32600)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"status","params":{"protocolVersion":3},"traceparent":123}""", -32600)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"status","params":{"protocolVersion":3},"extra":true}""", -32600)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"result":{},"method":"status","params":{"protocolVersion":3}}""", -32600)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"status","params":{"protocolVersion":3,"protocolVersion":3}}""", -32700)]
    [InlineData("""{"jsonrpc":"2.0","jsonrpc":"2.0","id":1,"method":"status","params":{"protocolVersion":3}}""", -32700)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"status","params":{"protocolVersion":"1"}}""", -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"status","params":{"protocolVersion":null}}""", -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"status","params":{"protocolVersion":2147483648}}""", -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"status"}""", -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"status","params":{}}""", -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"status","params":[1]}""", -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"status","params":{"protocolVersion":3,"extra":true}}""", -32602)]
    public async Task Malformed_requests_are_structured_failures_without_dispatch(string request, int code)
    {
        await using var fixture = new SocketFixture();
        await fixture.StartAsync();
        var response = await ExchangeAsync(fixture, request);
        AssertError(response, code);
        if (code is -32600 or -32700)
            Assert.Equal(JsonValueKind.Null, response.GetProperty("id").ValueKind);
        else
            Assert.Equal(1, response.GetProperty("id").GetInt32());
        Assert.Equal(0, fixture.Query.Calls);
        await fixture.Client().StatusAsync();
        Assert.Equal(1, fixture.Query.Calls);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("Status")]
    [InlineData("$/cancelRequest")]
    [InlineData("rpc.subscribe")]
    public async Task Unsupported_method_does_not_dispatch(string method)
    {
        await using var fixture = new SocketFixture();
        await fixture.StartAsync();
        var response = await ExchangeAsync(fixture,
            $$$"""{"jsonrpc":"2.0","id":7,"method":"{{{method}}}","params":{"protocolVersion":3}}""");
        AssertError(response, -32601);
        Assert.Equal(7, response.GetProperty("id").GetInt32());
        Assert.Equal(0, fixture.Query.Calls);
    }

    [Theory]
    [InlineData("status", 1)]
    [InlineData("status", 42)]
    [InlineData("unknown", 1)]
    public async Task Notifications_receive_no_reply_and_do_not_dispatch(string method, int version)
    {
        await using var fixture = new SocketFixture();
        await fixture.StartAsync();
        using var socket = await fixture.ConnectAsync();
        using var stream = new NetworkStream(socket);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await WriteRawAsync(stream, $$$"""{"jsonrpc":"2.0","method":"{{{method}}}","params":{"protocolVersion":{{{version}}}}}""", false, deadline.Token);
        Assert.Equal(0, await stream.ReadAsync(new byte[1], deadline.Token));
        Assert.Equal(0, fixture.Query.Calls);
        await fixture.Client().StatusAsync();
    }

    [Fact]
    public async Task Batch_is_rejected_as_a_whole_without_dispatching_any_item()
    {
        await using var fixture = new SocketFixture();
        await fixture.StartAsync();
        var response = await ExchangeAsync(fixture, $"[{StatusRequest},{StatusRequest}]");
        AssertError(response, -32600);
        Assert.Equal(JsonValueKind.Null, response.GetProperty("id").ValueKind);
        Assert.Equal(0, fixture.Query.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(16385)]
    [InlineData(int.MaxValue)]
    public async Task Invalid_frame_length_is_rejected_before_reading_or_allocating_body(int length)
    {
        await using var fixture = new SocketFixture();
        await fixture.StartAsync();
        using var socket = await fixture.ConnectAsync();
        using var stream = new NetworkStream(socket);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, length);
        await stream.WriteAsync(header, deadline.Token);
        AssertError(await ReadReplyAsync(stream, deadline.Token), -32600);
        Assert.Equal(0, fixture.Query.Calls);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("-9")]
    [InlineData("\"a-string-id\"")]
    public async Task Fragmented_frames_are_reassembled_and_ids_are_preserved(string id)
    {
        await using var fixture = new SocketFixture();
        await fixture.StartAsync();
        var response = await ExchangeAsync(fixture, StatusRequest.Replace("\"id\":1", $"\"id\":{id}"), fragment: true);
        Assert.Equal("2.0", response.GetProperty("jsonrpc").GetString());
        Assert.Equal(id, response.GetProperty("id").GetRawText());
        Assert.False(response.TryGetProperty("error", out _));
        Assert.Equal(LocalControlProtocol.CurrentVersion, response.GetProperty("result").GetProperty("protocolVersion").GetInt32());
        Assert.Equal("test-software-1.2.3", response.GetProperty("result").GetProperty("status").GetProperty("softwareVersion").GetString());
        Assert.Equal(1, fixture.Query.Calls);
    }

    [Fact]
    public async Task Pipelined_requests_cannot_dispatch_a_second_query()
    {
        await using var fixture = new SocketFixture();
        await fixture.StartAsync();
        using var socket = await fixture.ConnectAsync();
        using var stream = new NetworkStream(socket);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await stream.WriteAsync(Frame(StatusRequest).Concat(Frame(StatusRequest)).ToArray(), deadline.Token);
        var response = await ReadReplyAsync(stream, deadline.Token);
        Assert.Equal("running", response.GetProperty("result").GetProperty("status").GetProperty("state").GetString());
        try { Assert.Equal(0, await stream.ReadAsync(new byte[1], deadline.Token)); }
        catch (IOException error) when (error.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset }) { }
        Assert.Equal(1, fixture.Query.Calls);
    }

    [Fact]
    public async Task Truncated_request_does_not_dispatch_or_stop_the_service()
    {
        await using var fixture = new SocketFixture();
        await fixture.StartAsync();
        using var socket = await fixture.ConnectAsync();
        using var stream = new NetworkStream(socket);
        await stream.WriteAsync(new byte[] { 0, 0, 0, 20, (byte)'{' });
        socket.Shutdown(SocketShutdown.Send);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        AssertError(await ReadReplyAsync(stream, deadline.Token), -32600);
        Assert.Equal(0, fixture.Query.Calls);
        await fixture.Client().StatusAsync();
    }

    [Fact]
    public async Task Excessive_json_depth_is_bounded_without_dispatch()
    {
        await using var fixture = new SocketFixture();
        await fixture.StartAsync();
        var nested = new string('[', 20) + "0" + new string(']', 20);
        AssertError(await ExchangeAsync(fixture, nested), -32700);
        Assert.Equal(0, fixture.Query.Calls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    public async Task Application_version_gate_applies_to_every_registered_handler(int version)
    {
        await using var fixture = new SocketFixture();
        using var lease = await fixture.Endpoint.ListenAsync(CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exchange = ExchangeAsync(fixture,
            $$$"""{"jsonrpc":"2.0","id":1,"method":"probe","params":{"protocolVersion":{{{version}}}}}""");
        using (var socket = await lease.Listener.AcceptAsync(deadline.Token))
        using (var stream = new NetworkStream(socket))
        {
            await using var handler = new LocalControlMessageHandler(stream, true, 1, fixture.Endpoint.SocketPath, deadline.Token);
            using var rpc = new LocalControlRpc(handler);
            int calls = 0;
            rpc.AddLocalRpcMethod("probe", (int protocolVersion) => ++calls);
            rpc.StartListening();
            await handler.ResponseWritten.WaitAsync(deadline.Token);
            Assert.Equal(version == 1 ? 1 : 0, calls);
        }
        var response = await exchange;
        if (version == 1)
            Assert.Equal(1, response.GetProperty("result").GetInt32());
        else
            AssertError(response, -32001);
    }

    [Fact]
    public async Task Unexpected_application_failure_is_an_internal_error_and_the_server_survives()
    {
        await using var fixture = new SocketFixture();
        fixture.Query.Failure = new InvalidOperationException("Private implementation detail");
        await fixture.StartAsync();
        var response = await ExchangeAsync(fixture, StatusRequest);
        AssertError(response, -32603);
        Assert.Equal(1, response.GetProperty("id").GetInt32());
        Assert.DoesNotContain("Private implementation detail", response.GetRawText());
        Assert.Equal(1, fixture.Query.Calls);
        fixture.Query.Failure = null;
        await fixture.Client().StatusAsync();
        Assert.Equal(2, fixture.Query.Calls);
    }

    [Fact]
    public async Task Maximum_sized_frame_is_accepted_and_large_unknown_method_error_remains_bounded()
    {
        await using var fixture = new SocketFixture();
        await fixture.StartAsync();
        var response = await ExchangeAsync(fixture, StatusRequest.PadRight(LocalControlProtocol.MaximumFrameBytes));
        Assert.Equal(LocalControlProtocol.CurrentVersion, response.GetProperty("result").GetProperty("protocolVersion").GetInt32());
        var longMethod = StatusRequest.Replace("status", new string('x', 16000));
        var error = await ExchangeAsync(fixture, longMethod);
        AssertError(error, -32601);
        Assert.True(Encoding.UTF8.GetByteCount(error.GetRawText()) <= LocalControlProtocol.MaximumFrameBytes);
        await fixture.Client().StatusAsync();
        Assert.Equal(2, fixture.Query.Calls);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(42)]
    public async Task Reply_that_cannot_fit_its_escaped_id_closes_only_that_exchange(int version)
    {
        await using var fixture = new SocketFixture();
        await fixture.StartAsync();
        using var socket = await fixture.ConnectAsync();
        using var stream = new NetworkStream(socket);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = StatusRequest.Replace("\"id\":1", $"\"id\":\"{new string('é', 4000)}\"")
            .Replace("\"protocolVersion\":3", $"\"protocolVersion\":{version}");
        await WriteRawAsync(stream, request, false, deadline.Token);
        Assert.Equal(0, await stream.ReadAsync(new byte[1], deadline.Token));
        Assert.Equal(version == LocalControlProtocol.CurrentVersion ? 1 : 0, fixture.Query.Calls);
        await fixture.Client().StatusAsync();
        Assert.False(fixture.Server!.Completion.IsCompleted);
    }

    [Fact]
    public async Task Client_disconnect_before_request_does_not_dispatch_or_stop_the_server()
    {
        await using var fixture = new SocketFixture();
        await fixture.StartAsync();
        using (var socket = await fixture.ConnectAsync())
            socket.Shutdown(SocketShutdown.Both);
        await fixture.Client().StatusAsync();
        Assert.Equal(1, fixture.Query.Calls);
        Assert.False(fixture.Server!.Completion.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Slow_client_is_closed_at_deadline_while_other_clients_can_query(bool partial)
    {
        await using var fixture = new SocketFixture();
        // Leave room for cold library initialization under parallel test load, while staying below the production deadline.
        await fixture.StartAsync(timeout: TimeSpan.FromSeconds(1));
        using var slow = await fixture.ConnectAsync();
        if (partial)
            await slow.SendAsync(new byte[] { 0, 0, 0, 100, (byte)'{' });
        await fixture.Client().StatusAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal(0, await slow.ReceiveAsync(new byte[1], SocketFlags.None, deadline.Token));
        Assert.Equal(1, fixture.Query.Calls);
        await fixture.Client().StatusAsync();
        Assert.Equal(2, fixture.Query.Calls);
    }

    [Fact]
    public async Task Client_has_a_deadline_when_listener_does_not_reply()
    {
        await using var fixture = new SocketFixture();
        using var lease = await fixture.Endpoint.ListenAsync(CancellationToken.None);
        var watch = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<LocalControlException>(() => fixture.Client(timeout: TimeSpan.FromMilliseconds(200)).StatusAsync());
        AssertUnavailable(exception.Diagnostic, fixture);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("""{"jsonrpc":"1.0","id":1,"result":{}}""")]
    [InlineData("""{"id":1,"result":{}}""")]
    [InlineData("""{"jsonrpc":2,"id":1,"result":{}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"result":{},"error":{"code":-32600,"message":"bad"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"error":null}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"error":{"code":"bad","message":"bad"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"error":{"code":-32600,"message":null}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"error":{"code":-32601,"message":"Unknown method"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"error":{"code":-32001,"message":"Mismatch"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":3}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":3,"status":{"state":"running","softwareVersion":null}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"1"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"status","params":{"protocolVersion":3}}""")]
    public async Task Client_reports_malformed_or_error_responses_as_actionable_diagnostics(string body)
    {
        await using var fixture = new SocketFixture();
        var exception = await FakeResponseAsync<LocalControlException>(fixture, body);
        AssertUnavailable(exception.Diagnostic, fixture);
    }

    [Fact]
    public async Task Client_reports_version_mismatch_before_parsing_a_future_result_schema()
    {
        await using var fixture = new SocketFixture();
        var exception = await FakeResponseAsync<LocalControlException>(fixture,
            """{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":42,"futureResult":true}}""");
        Assert.Equal("protocol_mismatch", exception.Diagnostic.Code);
        Assert.Equal(LocalControlProtocol.CurrentVersion, exception.Diagnostic.ClientProtocolVersion);
        Assert.Equal(42, exception.Diagnostic.ServerProtocolVersion);
    }

    [Fact]
    public async Task Client_sends_JSON_RPC_and_reassembles_a_fragmented_result()
    {
        await using var fixture = new SocketFixture();
        using var lease = await fixture.Endpoint.ListenAsync(CancellationToken.None);
        var client = fixture.Client().StatusAsync();
        using var socket = await lease.Listener.AcceptAsync();
        using var stream = new NetworkStream(socket);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = await ReadReplyAsync(stream, deadline.Token);
        Assert.Equal("2.0", request.GetProperty("jsonrpc").GetString());
        Assert.Equal(JsonValueKind.Number, request.GetProperty("id").ValueKind);
        Assert.Equal("status", request.GetProperty("method").GetString());
        Assert.Equal(LocalControlProtocol.CurrentVersion, request.GetProperty("params").GetProperty("protocolVersion").GetInt32());
        Assert.False(request.TryGetProperty("command", out _));
        await WriteRawAsync(stream, StatusResult.Replace("\"id\":1", $"\"id\":{request.GetProperty("id").GetRawText()}"), true, deadline.Token);
        Assert.Equal("test", (await client).Status.SoftwareVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Client_rejects_oversized_or_truncated_response_frames(bool truncated)
    {
        await using var fixture = new SocketFixture();
        using var lease = await fixture.Endpoint.ListenAsync(CancellationToken.None);
        var client = fixture.Client().StatusAsync();
        using var socket = await lease.Listener.AcceptAsync();
        using var stream = new NetworkStream(socket);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await LocalControlProtocol.ReadAsync(stream, deadline.Token);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, truncated ? 100 : int.MaxValue);
        await stream.WriteAsync(header, deadline.Token);
        if (truncated)
            socket.Shutdown(SocketShutdown.Send);
        var exception = await Assert.ThrowsAsync<LocalControlException>(() => client);
        AssertUnavailable(exception.Diagnostic, fixture);
    }

    [Fact]
    public async Task Wrong_reply_id_cannot_satisfy_the_client_and_remains_bounded()
    {
        await using var fixture = new SocketFixture();
        var exception = await FakeResponseAsync<LocalControlException>(fixture, StatusResult.Replace("\"id\":1", "\"id\":999"), TimeSpan.FromMilliseconds(300));
        AssertUnavailable(exception.Diagnostic, fixture);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved_without_dispatch()
    {
        await using var fixture = new SocketFixture();
        await fixture.StartAsync();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Client().StatusAsync(cancellation.Token));
        Assert.Equal(0, fixture.Query.Calls);
    }

    [Fact]
    public async Task Cancellation_during_exchange_closes_connection_without_sending_a_notification()
    {
        await using var fixture = new SocketFixture();
        using var lease = await fixture.Endpoint.ListenAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var client = fixture.Client().StatusAsync(cancellation.Token);
        using var socket = await lease.Listener.AcceptAsync();
        using var stream = new NetworkStream(socket);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await LocalControlProtocol.ReadAsync(stream, deadline.Token);
        await cancellation.CancelAsync();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, await stream.ReadAsync(new byte[1], deadline.Token));
    }

    [Theory]
    [InlineData("--server")]
    [InlineData("--url")]
    [InlineData("--ssh")]
    [InlineData("--rabbitmq")]
    public async Task Remote_options_are_rejected_without_connecting(string option)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await AwctlCommand.RunAsync(["status", option, "example"], output, error));
        Assert.Contains("Usage: awctl status", error.ToString());
        Assert.Empty(output.ToString());
    }

    private static void AssertError(JsonElement response, int code)
    {
        Assert.Equal("2.0", response.GetProperty("jsonrpc").GetString());
        Assert.False(response.TryGetProperty("result", out _));
        Assert.Equal(code, response.GetProperty("error").GetProperty("code").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(response.GetProperty("error").GetProperty("message").GetString()));
    }

    private static void AssertUnavailable(ControlDiagnostic diagnostic, SocketFixture fixture)
    {
        Assert.Equal("control_plane_unavailable", diagnostic.Code);
        Assert.Equal(fixture.Endpoint.SocketPath, diagnostic.Endpoint);
        Assert.Contains("awctl service start", diagnostic.Guidance);
    }

    private static async Task<T> FakeResponseAsync<T>(SocketFixture fixture, string body, TimeSpan? timeout = null) where T : Exception
    {
        using var lease = await fixture.Endpoint.ListenAsync(CancellationToken.None);
        var client = fixture.Client(timeout: timeout).StatusAsync();
        using var socket = await lease.Listener.AcceptAsync();
        using var stream = new NetworkStream(socket);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = await ReadReplyAsync(stream, deadline.Token);
        await WriteRawAsync(stream, body.Replace("\"id\":1", $"\"id\":{request.GetProperty("id").GetRawText()}"), false, deadline.Token);
        return await Assert.ThrowsAsync<T>(() => client);
    }

    private static async Task<JsonElement> ExchangeAsync(SocketFixture fixture, string request, bool fragment = false)
    {
        using var socket = await fixture.ConnectAsync();
        using var stream = new NetworkStream(socket);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await WriteRawAsync(stream, request, fragment, deadline.Token);
        var reply = await ReadReplyAsync(stream, deadline.Token);
        Assert.Equal(0, await stream.ReadAsync(new byte[1], deadline.Token));
        return reply;
    }

    private static async Task<JsonElement> ReadReplyAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await LocalControlProtocol.ReadAsync(stream, cancellationToken));
        return document.RootElement.Clone();
    }

    private static byte[] Frame(string body)
    {
        byte[] payload = Encoding.UTF8.GetBytes(body);
        byte[] frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    private static async Task WriteRawAsync(Stream stream, string body, bool fragment, CancellationToken cancellationToken)
    {
        byte[] frame = Frame(body);
        if (fragment)
            foreach (byte item in frame)
                await stream.WriteAsync(new byte[] { item }, cancellationToken);
        else
            await stream.WriteAsync(frame, cancellationToken);
    }
}

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgenticWorkflows.Application;
using AgenticWorkflows.LocalControl;

namespace AgenticWorkflows.Tests;

public sealed class LocalControlTests
{
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
        Assert.Equal(1, result.RootElement.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("running", result.RootElement.GetProperty("status").GetProperty("state").GetString());
        Assert.Equal("test-software-1.2.3", result.RootElement.GetProperty("status").GetProperty("softwareVersion").GetString());
        Assert.Equal("", error.ToString());
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
        Assert.Equal("control_plane_unavailable", diagnostic.Code);
        Assert.Equal(fixture.Endpoint.SocketPath, diagnostic.Endpoint);
        Assert.Contains("dotnet run --project src/AgenticWorkflows.ControlPlane --no-build", diagnostic.Guidance);
        Assert.Empty(output.ToString());
        Assert.Equal(0, fixture.Query.Calls);
        Assert.Null(fixture.Server); // The client did not start a service.
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
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

    [Fact]
    public async Task Protocol_is_checked_before_interpreting_another_versions_command_schema()
    {
        await using var fixture = new SocketFixture();
        await fixture.StartAsync();
        var response = await ExchangeAsync(fixture, """{"protocolVersion":42,"futureOperation":{"different":true}}""");
        Assert.Equal("protocol_mismatch", response.Error?.Code);
        Assert.Equal(42, response.Error?.ClientProtocolVersion);
        Assert.Equal(1, response.Error?.ServerProtocolVersion);
        Assert.Null(response.Status);
        Assert.Equal(0, fixture.Query.Calls);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"protocolVersion\":\"1\",\"command\":\"status\"}")]
    [InlineData("{\"protocolVersion\":1}")]
    [InlineData("{\"protocolVersion\":1,\"command\":\"status\",\"extra\":true}")]
    [InlineData("{\"protocolVersion\":2,\"protocolVersion\":1,\"command\":\"status\"}")]
    public async Task Malformed_requests_are_structured_failures_without_dispatch(string request)
    {
        await using var fixture = new SocketFixture();
        await fixture.StartAsync();
        var response = await ExchangeAsync(fixture, request);
        Assert.Equal("invalid_request", response.Error?.Code);
        Assert.Null(response.Status);
        Assert.Equal(1, response.ProtocolVersion);
        Assert.Equal(0, fixture.Query.Calls);
        await fixture.Client().StatusAsync();
        Assert.Equal(1, fixture.Query.Calls);
    }

    [Fact]
    public async Task Unsupported_command_does_not_dispatch()
    {
        await using var fixture = new SocketFixture();
        await fixture.StartAsync();
        var response = await ExchangeAsync(fixture, """{"protocolVersion":1,"command":"start"}""");
        Assert.Equal("unknown_command", response.Error?.Code);
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
        var response = LocalControlProtocol.Deserialize<Response>(await LocalControlProtocol.ReadAsync(stream, deadline.Token));
        Assert.Equal("invalid_request", response.Error?.Code);
        Assert.Equal(0, fixture.Query.Calls);
    }

    [Fact]
    public async Task Fragmented_frames_are_reassembled_and_only_one_query_runs_per_connection()
    {
        await using var fixture = new SocketFixture();
        await fixture.StartAsync();
        var response = await ExchangeAsync(fixture, """{"protocolVersion":1,"command":"status"}""", fragment: true);
        Assert.Equal("test-software-1.2.3", response.Status?.SoftwareVersion);
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
        var response = LocalControlProtocol.Deserialize<Response>(await LocalControlProtocol.ReadAsync(stream, deadline.Token));
        Assert.Equal("invalid_request", response.Error?.Code);
        Assert.Equal(0, fixture.Query.Calls);
        await fixture.Client().StatusAsync();
    }

    [Fact]
    public async Task Slow_client_is_closed_at_deadline_while_other_clients_can_query()
    {
        await using var fixture = new SocketFixture();
        await fixture.StartAsync(timeout: TimeSpan.FromMilliseconds(300));
        using var slow = await fixture.ConnectAsync();
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
        Assert.Equal("control_plane_unavailable", exception.Diagnostic.Code);
        Assert.Equal(fixture.Endpoint.SocketPath, exception.Diagnostic.Endpoint);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"protocolVersion\":1}")]
    [InlineData("{\"protocolVersion\":1,\"status\":{\"state\":\"running\",\"softwareVersion\":null}}")]
    [InlineData("{\"protocolVersion\":\"1\"}")]
    public async Task Client_reports_malformed_responses_as_actionable_diagnostics(string body)
    {
        await using var fixture = new SocketFixture();
        using var lease = await fixture.Endpoint.ListenAsync(CancellationToken.None);
        var client = fixture.Client().StatusAsync();
        using var socket = await lease.Listener.AcceptAsync();
        using var stream = new NetworkStream(socket);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await LocalControlProtocol.ReadAsync(stream, deadline.Token);
        await WriteRawAsync(stream, body, false, deadline.Token);
        var exception = await Assert.ThrowsAsync<LocalControlException>(() => client);
        Assert.Equal("control_plane_unavailable", exception.Diagnostic.Code);
        Assert.Equal(fixture.Endpoint.SocketPath, exception.Diagnostic.Endpoint);
    }

    [Fact]
    public async Task Client_reports_version_mismatch_before_parsing_a_future_reply_schema()
    {
        await using var fixture = new SocketFixture();
        using var lease = await fixture.Endpoint.ListenAsync(CancellationToken.None);
        var client = fixture.Client().StatusAsync();
        using var socket = await lease.Listener.AcceptAsync();
        using var stream = new NetworkStream(socket);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await LocalControlProtocol.ReadAsync(stream, deadline.Token);
        await WriteRawAsync(stream, """{"protocolVersion":42,"futureResult":true}""", false, deadline.Token);
        var exception = await Assert.ThrowsAsync<LocalControlException>(() => client);
        Assert.Equal("protocol_mismatch", exception.Diagnostic.Code);
        Assert.Equal(1, exception.Diagnostic.ClientProtocolVersion);
        Assert.Equal(42, exception.Diagnostic.ServerProtocolVersion);
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
        Assert.Equal("control_plane_unavailable", exception.Diagnostic.Code);
        Assert.Equal(fixture.Endpoint.SocketPath, exception.Diagnostic.Endpoint);
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

    private static async Task<Response> ExchangeAsync(SocketFixture fixture, string request, bool fragment = false)
    {
        using var socket = await fixture.ConnectAsync();
        using var stream = new NetworkStream(socket);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await WriteRawAsync(stream, request, fragment, deadline.Token);
        var reply = LocalControlProtocol.Deserialize<Response>(await LocalControlProtocol.ReadAsync(stream, deadline.Token));
        Assert.Equal(0, await stream.ReadAsync(new byte[1], deadline.Token));
        return reply;
    }

    private static async Task WriteRawAsync(Stream stream, string body, bool fragment, CancellationToken cancellationToken)
    {
        byte[] payload = Encoding.UTF8.GetBytes(body);
        byte[] frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        if (fragment)
            foreach (byte item in frame)
                await stream.WriteAsync(new byte[] { item }, cancellationToken);
        else
            await stream.WriteAsync(frame, cancellationToken);
    }
}

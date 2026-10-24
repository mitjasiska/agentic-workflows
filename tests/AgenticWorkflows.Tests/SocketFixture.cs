using System.Net.Sockets;
using System.Runtime.Versioning;
using AgenticWorkflows.Application;
using AgenticWorkflows.LocalControl;

[assembly: SupportedOSPlatform("linux")]

namespace AgenticWorkflows.Tests;

internal sealed class SocketFixture : IAsyncDisposable
{
    internal string Root { get; } = Path.Combine("/tmp", $"aw-test-{Guid.NewGuid():N}");
    internal LocalControlEndpoint Endpoint { get; }
    internal CountingQuery Query { get; } = new();
    internal LocalControlServer? Server { get; private set; }

    internal SocketFixture()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("These integration tests require Linux Unix sockets.");
        Directory.CreateDirectory(Root, LinuxFileSystem.PrivateDirectory);
        Endpoint = new(Path.Combine(Root, "runtime"));
    }

    internal async Task StartAsync(int protocolVersion = 1, TimeSpan? timeout = null)
    {
        Server = new(Query, Endpoint, protocolVersion, timeout ?? TimeSpan.FromSeconds(5));
        await Server.StartAsync();
    }

    internal LocalControlClient Client(int version = 1, TimeSpan? timeout = null) =>
        new(Endpoint, version, timeout ?? TimeSpan.FromSeconds(5));

    internal async Task<Socket> ConnectAsync()
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(Endpoint.SocketPath));
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Server is not null)
                await Server.DisposeAsync();
        }
        finally { Directory.Delete(Root, recursive: true); }
    }

    internal sealed class CountingQuery : IControlPlaneStatusQuery
    {
        private int calls;
        internal int Calls => Volatile.Read(ref calls);

        public ControlPlaneStatus Execute()
        {
            Interlocked.Increment(ref calls);
            return new("running", "test-software-1.2.3");
        }
    }
}

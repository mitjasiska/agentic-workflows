using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;

namespace AgenticWorkflows.LocalControl;

[SupportedOSPlatform("linux")]
public sealed class LocalControlEndpoint
{
    public string SocketPath { get; }
    internal string RuntimeDirectory { get; }
    private readonly string? runtimeParent;

    internal LocalControlEndpoint(string runtimeDirectory, string? runtimeParent = null)
    {
        if (!Path.IsPathFullyQualified(runtimeDirectory) || runtimeDirectory.Contains('\0'))
            throw new IOException("The local runtime directory must be an absolute filesystem path.");
        RuntimeDirectory = Path.GetFullPath(runtimeDirectory);
        SocketPath = Path.Combine(RuntimeDirectory, "control.sock");
        this.runtimeParent = runtimeParent;
        if (Encoding.UTF8.GetByteCount(SocketPath) > 107)
            throw new IOException($"Local socket path '{SocketPath}' exceeds Linux's 107-byte pathname limit. Use a shorter XDG_RUNTIME_DIR.");
    }

    public static LocalControlEndpoint Discover() => FromRuntimeDirectory(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"));

    internal static LocalControlEndpoint FromRuntimeDirectory(string? xdgRuntimeDirectory) =>
        string.IsNullOrEmpty(xdgRuntimeDirectory)
            ? new($"/tmp/agentic-workflows-{LinuxFileSystem.UserId}")
            : new(Path.Combine(xdgRuntimeDirectory, "agentic-workflows"), xdgRuntimeDirectory);

    internal void ValidateForClient()
    {
        ValidateParent();
        LinuxFileSystem.Require(RuntimeDirectory, LinuxFileSystem.DirectoryType, LinuxFileSystem.PrivateDirectory);
        LinuxFileSystem.Require(SocketPath, LinuxFileSystem.SocketType, LinuxFileSystem.PrivateSocket);
    }

    private void ValidateParent()
    {
        if (runtimeParent is not null)
            LinuxFileSystem.Require(runtimeParent, LinuxFileSystem.DirectoryType, LinuxFileSystem.PrivateDirectory);
    }

    internal async Task<EndpointLease> ListenAsync(CancellationToken cancellationToken)
    {
        ValidateParent();
        LinuxFileSystem.CreatePrivateDirectory(RuntimeDirectory);
        var directoryLock = LinuxFileSystem.LockDirectory(RuntimeDirectory);
        Socket? listener = null;
        LinuxFileSystem.Metadata? bound = null;
        try
        {
            await RemoveStaleSocketAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            listener = LinuxFileSystem.Bind(SocketPath);
            bound = LinuxFileSystem.Require(SocketPath, LinuxFileSystem.SocketType);
            File.SetUnixFileMode(SocketPath, LinuxFileSystem.PrivateSocket);
            listener.Listen(16);
            return new EndpointLease(this, listener, directoryLock, bound.Value);
        }
        catch
        {
            listener?.Dispose();
            try { RemoveOwnedSocket(bound); }
            finally { directoryLock.Dispose(); }
            throw;
        }
    }

    private async Task RemoveStaleSocketAsync(CancellationToken cancellationToken)
    {
        if (LinuxFileSystem.Inspect(SocketPath) is null)
            return;
        var stale = LinuxFileSystem.Require(SocketPath, LinuxFileSystem.SocketType);
        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(LocalControlProtocol.ExchangeTimeout);
        try
        {
            await probe.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), deadline.Token);
        }
        catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionRefused)
        {
            // Only a positively refused, unchanged, owned socket is stale.
            if (!stale.SameFile(LinuxFileSystem.Require(SocketPath, LinuxFileSystem.SocketType)))
                throw new IOException($"Local socket '{SocketPath}' changed during startup; retry after checking the control plane.");
            File.Delete(SocketPath);
            return;
        }
        throw new IOException($"Local socket '{SocketPath}' is in use. Check the running control plane before restarting.");
    }

    private void RemoveOwnedSocket(LinuxFileSystem.Metadata? bound)
    {
        if (bound is not null && LinuxFileSystem.Inspect(SocketPath) is { } current &&
            (current.Mode & 0xf000) == LinuxFileSystem.SocketType && bound.Value.SameFile(current))
            File.Delete(SocketPath);
    }

    internal sealed class EndpointLease(LocalControlEndpoint endpoint, Socket listener,
        LinuxFileSystem.DirectoryLease directoryLock, LinuxFileSystem.Metadata bound) : IDisposable
    {
        internal Socket Listener { get; } = listener;

        public void Dispose()
        {
            Listener.Dispose();
            try { endpoint.RemoveOwnedSocket(bound); }
            finally { directoryLock.Dispose(); }
        }
    }
}

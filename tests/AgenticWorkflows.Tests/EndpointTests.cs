using System.Net.Sockets;
using AgenticWorkflows.LocalControl;

namespace AgenticWorkflows.Tests;

public sealed class EndpointTests
{
    [Fact]
    public async Task Endpoint_is_owned_private_and_removed_on_shutdown()
    {
        await using var fixture = new SocketFixture();
        await fixture.StartAsync();
        var directory = LinuxFileSystem.Require(fixture.Endpoint.RuntimeDirectory, LinuxFileSystem.DirectoryType);
        var socket = LinuxFileSystem.Require(fixture.Endpoint.SocketPath, LinuxFileSystem.SocketType);
        Assert.Equal(LinuxFileSystem.UserId, directory.UserId);
        Assert.Equal(LinuxFileSystem.UserId, socket.UserId);
        Assert.Equal(LinuxFileSystem.PrivateDirectory, File.GetUnixFileMode(fixture.Endpoint.RuntimeDirectory));
        Assert.Equal(LinuxFileSystem.PrivateSocket, File.GetUnixFileMode(fixture.Endpoint.SocketPath));
        await fixture.Server!.DisposeAsync();
        Assert.Null(LinuxFileSystem.Inspect(fixture.Endpoint.SocketPath));
        Assert.True(Directory.Exists(fixture.Endpoint.RuntimeDirectory));
        await fixture.StartAsync();
        await fixture.Client().StatusAsync();
    }

    [Fact]
    public async Task Owned_stale_socket_is_replaced_and_serves_status()
    {
        await using var fixture = new SocketFixture();
        LinuxFileSystem.CreatePrivateDirectory(fixture.Endpoint.RuntimeDirectory);
        using (var stale = LinuxFileSystem.Bind(fixture.Endpoint.SocketPath))
            File.SetUnixFileMode(fixture.Endpoint.SocketPath, LinuxFileSystem.PrivateSocket);
        Assert.NotNull(LinuxFileSystem.Inspect(fixture.Endpoint.SocketPath));
        await fixture.StartAsync();
        Assert.Equal("running", (await fixture.Client().StatusAsync()).Status.State);
    }

    [Fact]
    public async Task Second_server_cannot_unlink_a_live_server()
    {
        await using var fixture = new SocketFixture();
        await fixture.StartAsync();
        var original = LinuxFileSystem.Inspect(fixture.Endpoint.SocketPath)!.Value;
        await using var second = new LocalControlServer(fixture.Query, fixture.Endpoint);
        var exception = await Assert.ThrowsAsync<IOException>(() => second.StartAsync());
        Assert.Contains("Another control plane", exception.Message);
        Assert.True(original.SameFile(LinuxFileSystem.Inspect(fixture.Endpoint.SocketPath)!.Value));
        await fixture.Client().StatusAsync();
        Assert.Equal(1, fixture.Query.Calls);
    }

    [Fact]
    public async Task Live_socket_without_our_directory_lock_is_preserved()
    {
        await using var fixture = new SocketFixture();
        LinuxFileSystem.CreatePrivateDirectory(fixture.Endpoint.RuntimeDirectory);
        using var foreignListener = LinuxFileSystem.Bind(fixture.Endpoint.SocketPath);
        foreignListener.Listen(1);
        var original = LinuxFileSystem.Inspect(fixture.Endpoint.SocketPath)!.Value;
        var exception = await Assert.ThrowsAsync<IOException>(() => fixture.StartAsync());
        Assert.Contains("is in use", exception.Message);
        Assert.True(original.SameFile(LinuxFileSystem.Inspect(fixture.Endpoint.SocketPath)!.Value));
        using var connection = await fixture.ConnectAsync();
        Assert.True(connection.Connected);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("symlink")]
    [InlineData("dangling-symlink")]
    public async Task Startup_preserves_non_socket_entries(string kind)
    {
        await using var fixture = new SocketFixture();
        LinuxFileSystem.CreatePrivateDirectory(fixture.Endpoint.RuntimeDirectory);
        string sentinel = Path.Combine(fixture.Root, "sentinel");
        await File.WriteAllTextAsync(sentinel, "keep this data");
        switch (kind)
        {
            case "file": await File.WriteAllTextAsync(fixture.Endpoint.SocketPath, "keep this data"); break;
            case "directory": Directory.CreateDirectory(fixture.Endpoint.SocketPath); break;
            case "symlink": File.CreateSymbolicLink(fixture.Endpoint.SocketPath, sentinel); break;
            case "dangling-symlink": File.CreateSymbolicLink(fixture.Endpoint.SocketPath, sentinel + ".missing"); break;
        }
        var original = LinuxFileSystem.Inspect(fixture.Endpoint.SocketPath)!.Value;
        await Assert.ThrowsAsync<IOException>(() => fixture.StartAsync());
        Assert.True(original.SameFile(LinuxFileSystem.Inspect(fixture.Endpoint.SocketPath)!.Value));
        Assert.Equal("keep this data", await File.ReadAllTextAsync(sentinel));
    }

    [Fact]
    public async Task Runtime_symlink_is_refused_without_modifying_target()
    {
        await using var fixture = new SocketFixture();
        string target = Path.Combine(fixture.Root, "target");
        Directory.CreateDirectory(target, LinuxFileSystem.PrivateDirectory);
        Directory.CreateSymbolicLink(fixture.Endpoint.RuntimeDirectory, target);
        await Assert.ThrowsAsync<IOException>(() => fixture.StartAsync());
        Assert.Empty(Directory.GetFileSystemEntries(target));
        Assert.Equal(target, new DirectoryInfo(fixture.Endpoint.RuntimeDirectory).LinkTarget);
    }

    [Fact]
    public async Task Insecure_runtime_permissions_are_refused_without_chmod()
    {
        await using var fixture = new SocketFixture();
        LinuxFileSystem.CreatePrivateDirectory(fixture.Endpoint.RuntimeDirectory);
        var insecure = LinuxFileSystem.PrivateDirectory | UnixFileMode.GroupRead;
        File.SetUnixFileMode(fixture.Endpoint.RuntimeDirectory, insecure);
        await Assert.ThrowsAsync<IOException>(() => fixture.StartAsync());
        Assert.Equal(insecure, File.GetUnixFileMode(fixture.Endpoint.RuntimeDirectory));
        Assert.Null(LinuxFileSystem.Inspect(fixture.Endpoint.SocketPath));
    }

    [Fact]
    public async Task Client_refuses_a_socket_with_group_access_without_dispatch()
    {
        await using var fixture = new SocketFixture();
        await fixture.StartAsync();
        File.SetUnixFileMode(fixture.Endpoint.SocketPath, LinuxFileSystem.PrivateSocket | UnixFileMode.GroupWrite);
        var error = await Assert.ThrowsAsync<LocalControlException>(() => fixture.Client().StatusAsync());
        Assert.Contains("Unsafe", error.Message);
        Assert.Equal(0, fixture.Query.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_preserves_a_replacement_at_the_socket_path(bool socketReplacement)
    {
        await using var fixture = new SocketFixture();
        await fixture.StartAsync();
        File.Delete(fixture.Endpoint.SocketPath);
        using var replacement = socketReplacement ? LinuxFileSystem.Bind(fixture.Endpoint.SocketPath) : null;
        if (!socketReplacement)
            await File.WriteAllTextAsync(fixture.Endpoint.SocketPath, "replacement data");
        var original = LinuxFileSystem.Inspect(fixture.Endpoint.SocketPath)!.Value;
        await fixture.Server!.DisposeAsync();
        Assert.True(original.SameFile(LinuxFileSystem.Inspect(fixture.Endpoint.SocketPath)!.Value));
        if (!socketReplacement)
            Assert.Equal("replacement data", await File.ReadAllTextAsync(fixture.Endpoint.SocketPath));
    }

    [Fact]
    public async Task Shutdown_cancels_idle_connections_and_releases_directory_lock()
    {
        await using var fixture = new SocketFixture();
        await fixture.StartAsync();
        using var idle = await fixture.ConnectAsync();
        await fixture.Server!.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(LinuxFileSystem.Inspect(fixture.Endpoint.SocketPath));
        await fixture.StartAsync();
        await fixture.Client().StatusAsync();
    }

    [Fact]
    public void Discovery_uses_xdg_or_a_deterministic_per_user_fallback()
    {
        Assert.Equal("/run/user/123/agentic-workflows/control.sock",
            LocalControlEndpoint.FromRuntimeDirectory("/run/user/123").SocketPath);
        Assert.Equal($"/tmp/agentic-workflows-{LinuxFileSystem.UserId}/control.sock",
            LocalControlEndpoint.FromRuntimeDirectory(null).SocketPath);
        Assert.Equal(LocalControlEndpoint.FromRuntimeDirectory(null).SocketPath,
            LocalControlEndpoint.FromRuntimeDirectory("").SocketPath);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("/tmp/contains\0nul")]
    public void Invalid_runtime_paths_are_rejected(string path) =>
        Assert.Throws<IOException>(() => LocalControlEndpoint.FromRuntimeDirectory(path));

    [Fact]
    public void Overlong_utf8_path_is_rejected_with_recovery_guidance()
    {
        var error = Assert.Throws<IOException>(() => LocalControlEndpoint.FromRuntimeDirectory("/tmp/" + new string('é', 60)));
        Assert.Contains("107-byte", error.Message);
        Assert.Contains("shorter XDG_RUNTIME_DIR", error.Message);
    }

    [Fact]
    public async Task Xdg_parent_must_be_owned_private_and_physical()
    {
        await using var fixture = new SocketFixture();
        string path = Path.Combine(fixture.Root, "xdg");
        Directory.CreateSymbolicLink(path, fixture.Root);
        var endpoint = LocalControlEndpoint.FromRuntimeDirectory(path);
        await Assert.ThrowsAsync<IOException>(() => endpoint.ListenAsync(CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "agentic-workflows")));
    }
}

using System.Runtime.InteropServices;
using AgenticWorkflows.LocalControl;
using Microsoft.Win32.SafeHandles;

namespace AgenticWorkflows.Tests;

public sealed class DirectoryLeaseTests
{
    [Fact]
    public async Task Lease_disposal_unlocks_even_while_a_duplicate_open_file_description_survives()
    {
        await using var fixture = new SocketFixture();
        using var lease = LinuxFileSystem.LockDirectory(fixture.Root);
        // fork inherits the same open file description as dup. Keep a duplicate alive
        // deterministically instead of depending on the duration of a fork-to-exec race.
        using var inherited = new SafeFileHandle(fcntl(lease.Handle, 1030, 0), ownsHandle: true); // F_DUPFD_CLOEXEC
        Assert.False(inherited.IsInvalid);
        Assert.Throws<IOException>(() => LinuxFileSystem.LockDirectory(fixture.Root));
        lease.Dispose();
        using var next = LinuxFileSystem.LockDirectory(fixture.Root);
        lease.Dispose(); // Disposing the old lease again cannot release its successor.
        Assert.Throws<IOException>(() => LinuxFileSystem.LockDirectory(fixture.Root));
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(SafeFileHandle descriptor, int command, int argument);
}

using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace AgenticWorkflows.LocalControl;

/// <summary>Linux ownership, no-follow metadata, and process-lifetime locking in one place.</summary>
[SupportedOSPlatform("linux")]
internal static class LinuxFileSystem
{
    internal const ushort DirectoryType = 0x4000;
    internal const ushort SocketType = 0xc000;
    internal const ushort RegularFileType = 0x8000;
    internal const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    internal const UnixFileMode PrivateSocket = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    internal static uint UserId => geteuid();

    // statx has a fixed 256-byte Linux ABI, unlike architecture-dependent struct stat.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    internal struct Metadata
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(20)] internal uint UserId;
        [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(32)] internal ulong Inode;
        [FieldOffset(136)] internal uint DeviceMajor;
        [FieldOffset(140)] internal uint DeviceMinor;

        internal readonly bool SameFile(Metadata other) =>
            Inode == other.Inode && DeviceMajor == other.DeviceMajor && DeviceMinor == other.DeviceMinor;
    }

    internal static Metadata? Inspect(string path)
    {
        if (statx(-100, path, 0x100, 0x10b, out var metadata) == 0) // AT_FDCWD, AT_SYMLINK_NOFOLLOW
        {
            if ((metadata.Mask & 0x10b) != 0x10b)
                throw new IOException($"Cannot verify ownership and identity of '{path}'.");
            return metadata;
        }
        int error = Marshal.GetLastPInvokeError();
        if (error == 2) // ENOENT; other errors must never authorize deletion.
            return null;
        throw Failure($"inspect '{path}'", error);
    }

    internal static Metadata Require(string path, ushort type, UnixFileMode? permissions = null)
    {
        var metadata = Inspect(path) ?? throw new FileNotFoundException($"Local endpoint path is missing: '{path}'.", path);
        if ((metadata.Mode & 0xf000) != type || metadata.UserId != UserId ||
            (permissions is not null && (metadata.Mode & 0xfff) != (ushort)permissions.Value))
            throw new IOException($"Unsafe local endpoint path '{path}': expected an owned " +
                $"{(type == DirectoryType ? "directory" : "socket")}" +
                (permissions is null ? "." : $" with mode {Convert.ToString((int)permissions.Value, 8)}; inspect its ownership and permissions."));
        return metadata;
    }

    internal static void CreatePrivateDirectory(string path)
    {
        // mkdir is atomic and never chmods an existing directory or follows a final symlink.
        if (mkdir(path, (uint)PrivateDirectory) != 0 && Marshal.GetLastPInvokeError() != 17) // EEXIST
            throw Failure($"create runtime directory '{path}'", Marshal.GetLastPInvokeError());
        Require(path, DirectoryType, PrivateDirectory);
    }

    internal static SafeFileHandle LockDirectory(string path)
    {
        var before = Require(path, DirectoryType, PrivateDirectory);
        int descriptor = open(path, 0x10000 | 0x20000 | 0x80000); // O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC
        if (descriptor < 0)
            throw Failure($"open runtime directory '{path}'", Marshal.GetLastPInvokeError());
        var handle = new SafeFileHandle(descriptor, ownsHandle: true);
        try
        {
            if (flock(handle, 2 | 4) != 0) // LOCK_EX | LOCK_NB; released by the kernel even after a crash.
                throw new IOException($"Another control plane holds runtime directory '{path}'. Check the running control plane before restarting.");
            if (statx(descriptor, "", 0x1000, 0x10b, out var opened) != 0 || // AT_EMPTY_PATH
                !before.SameFile(opened) || !before.SameFile(Require(path, DirectoryType, PrivateDirectory)))
                throw new IOException($"Runtime directory '{path}' changed during startup; inspect it before retrying.");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static SafeFileHandle OpenRegularRead(string path)
    {
        var before = Require(path, RegularFileType);
        int descriptor = open(path, 0x800 | 0x20000 | 0x80000); // O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC
        if (descriptor < 0) throw Failure("open regular delivery file", Marshal.GetLastPInvokeError());
        var handle = new SafeFileHandle(descriptor, ownsHandle: true);
        try
        {
            if (statx(descriptor, "", 0x1000, 0x10b, out var opened) != 0 ||
                !before.SameFile(opened) || (opened.Mode & 0xf000) != RegularFileType || opened.UserId != UserId)
                throw new IOException("Result delivery changed identity while being opened.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    internal static Socket Bind(string path)
    {
        // Bind before wrapping the handle: Socket.Bind(EndPoint) registers unconditional
        // pathname deletion on Dispose. Our lease instead removes only its own inode.
        int descriptor = socket(1, 1 | 0x80000, 0); // AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC
        if (descriptor < 0)
            throw Failure("create local socket", Marshal.GetLastPInvokeError());
        var handle = new SafeSocketHandle(descriptor, ownsHandle: true);
        try
        {
            var address = new UnixDomainSocketEndPoint(path).Serialize();
            if (bind(handle, address.Buffer.ToArray(), address.Size) != 0)
                throw Failure($"bind '{path}'", Marshal.GetLastPInvokeError());
            return new Socket(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static IOException Failure(string action, int error) =>
        new($"Could not {action}: {new Win32Exception(error).Message} (errno {error}).");

    [DllImport("libc")] private static extern uint geteuid();
    [DllImport("libc", SetLastError = true)] private static extern int statx(int dirfd, string path, int flags, uint mask, out Metadata metadata);
    [DllImport("libc", SetLastError = true)] private static extern int mkdir(string path, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int flock(SafeFileHandle descriptor, int operation);
    [DllImport("libc", SetLastError = true)] private static extern int socket(int domain, int type, int protocol);
    [DllImport("libc", SetLastError = true)] private static extern int bind(SafeSocketHandle descriptor, byte[] address, int length);
}

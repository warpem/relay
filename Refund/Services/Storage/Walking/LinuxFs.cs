using System.Runtime.InteropServices;

namespace Refund.Services.Storage.Walking;

/// <summary>The few libc calls the Linux walker needs. Layouts and flags are for 64-bit Linux (x64 and arm64).</summary>
internal static unsafe partial class LinuxFs
{
    public static readonly bool IsSupported = OperatingSystem.IsLinux() &&
        RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64 && Probe();

    private static bool IsArm => RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    public const int AT_FDCWD = -100;
    public const int AT_SYMLINK_NOFOLLOW = 0x100, AT_NO_AUTOMOUNT = 0x800, AT_EMPTY_PATH = 0x1000, AT_STATX_DONT_SYNC = 0x4000;
    public const uint STATX_TYPE = 0x1, STATX_MODE = 0x2, STATX_NLINK = 0x4, STATX_MTIME = 0x40, STATX_INO = 0x100,
        STATX_SIZE = 0x200, STATX_BLOCKS = 0x400;
    public const uint WalkMask = STATX_TYPE | STATX_MODE | STATX_NLINK | STATX_MTIME | STATX_INO | STATX_SIZE | STATX_BLOCKS;
    public const int O_RDONLY = 0, O_CLOEXEC = 0x80000;
    public static int O_DIRECTORY => IsArm ? 0x4000 : 0x10000;
    public static int O_NOFOLLOW => IsArm ? 0x8000 : 0x20000;
    public const ushort S_IFMT = 0xF000, S_IFDIR = 0x4000, S_IFREG = 0x8000, S_IFLNK = 0xA000;
    public const int ENOENT = 2, EACCES = 13, ENOTDIR = 20, ELOOP = 40;
    public const byte DT_DIR = 4;

    /// <summary>struct statx (256 bytes, the same on every architecture).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    public struct Statx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint NLink;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Ino;
        [FieldOffset(40)] public ulong Size;
        [FieldOffset(48)] public ulong Blocks;
        [FieldOffset(112)] public long MtimeSeconds;
        [FieldOffset(120)] public uint MtimeNanoseconds;
        [FieldOffset(136)] public uint DevMajor;
        [FieldOffset(140)] public uint DevMinor;

        public readonly ushort Type => (ushort)(Mode & S_IFMT);
        public readonly ulong Dev => ((ulong)DevMajor << 32) | DevMinor;
        public readonly long MtimeTicks => MtimeSeconds <= 0 ? 0 :
            DateTime.UnixEpoch.Ticks + MtimeSeconds * TimeSpan.TicksPerSecond + MtimeNanoseconds / 100;
    }

    /// <summary>struct dirent64: d_ino, d_off, d_reclen, d_type, then the NUL-terminated name.</summary>
    public const int DirentTypeOffset = 18, DirentNameOffset = 19;

    /// <summary>struct statvfs on 64-bit Linux: eleven 8-byte fields, then padding.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    public struct StatVfs
    {
        [FieldOffset(0)] public ulong BlockSize;
        [FieldOffset(8)] public ulong FragmentSize;
        [FieldOffset(16)] public ulong Blocks;
        [FieldOffset(24)] public ulong BlocksFree;
        [FieldOffset(32)] public ulong BlocksAvailable;
        [FieldOffset(40)] public ulong Files;
        [FieldOffset(48)] public ulong FilesFree;
        [FieldOffset(56)] public ulong FilesAvailable;
        [FieldOffset(64)] public ulong FsId;
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true)]
    public static partial int OpenAt(int dirFd, byte* name, int flags);

    [LibraryImport("libc", EntryPoint = "close")]
    public static partial int Close(int fd);

    [LibraryImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
    public static partial nint FdOpenDir(int fd);

    [LibraryImport("libc", EntryPoint = "readdir64", SetLastError = true)]
    public static partial byte* ReadDir(nint dir);

    [LibraryImport("libc", EntryPoint = "closedir")]
    public static partial int CloseDir(nint dir);

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true)]
    public static partial int StatxAt(int dirFd, byte* name, int flags, uint mask, Statx* result);

    [LibraryImport("libc", EntryPoint = "statvfs", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int StatVfsOf(string path, StatVfs* result);

    [LibraryImport("libc", EntryPoint = "setpriority", SetLastError = true)]
    public static partial int SetPriority(int which, int who, int priority);

    [LibraryImport("libc", EntryPoint = "syscall")]
    public static partial long Syscall(long number);

    /// <summary>The calling thread's kernel id (glibc 2.28 has no gettid wrapper).</summary>
    public static int CurrentThreadId() => (int)Syscall(IsArm ? 178 : 186);

    /// <summary>Lowers the calling thread's priority to the minimum (nice 19). Linux applies nice per thread.</summary>
    public static bool LowerCurrentThreadPriority() => SetPriority(0 /* PRIO_PROCESS */, CurrentThreadId(), 19) == 0;

    // statx needs glibc 2.28 and Linux 4.11; older systems use the managed walker.
    private static bool Probe()
    {
        try
        {
            Statx s;
            byte empty = 0;
            return StatxAt(AT_FDCWD, &empty, AT_EMPTY_PATH, STATX_TYPE, &s) == 0 || Marshal.GetLastPInvokeError() != 38 /* ENOSYS */;
        }
        catch (EntryPointNotFoundException) { return false; }
        catch (DllNotFoundException) { return false; }
    }
}

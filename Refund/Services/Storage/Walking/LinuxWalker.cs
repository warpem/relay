using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using static Refund.Services.Storage.Walking.LinuxFs;

namespace Refund.Services.Storage.Walking;

/// <summary>
/// Measures a folder tree with readdir and statx, each lookup relative to its parent folder's descriptor: no path
/// strings, no symlink following (links are counted, not entered), no crossing into other mounts, and
/// AT_STATX_DONT_SYNC so network file systems may answer from their attribute caches. Allocates nothing per entry.
/// </summary>
internal static unsafe class LinuxWalker
{
    private const int BatchSize = 64;

    [ThreadStatic] private static Frame[] t_frames;

    private struct Frame
    {
        public nint Dir;
        public int Fd, Node, Depth, Tag;
    }

    /// <summary>
    /// Walks <paramref name="root"/> into <paramref name="state"/> (already begun). Returns false if the root isn't a
    /// readable folder (missing, a file, a link).
    /// </summary>
    public static bool Walk(string root, WalkState state)
    {
        int rootFd = Open(root, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
        if (rootFd < 0)
            return false;
        Statx rootStat;
        byte empty = 0;
        if (StatxAt(rootFd, &empty, AT_EMPTY_PATH | AT_STATX_DONT_SYNC, WalkMask, &rootStat) != 0)
        {
            Close(rootFd);
            return false;
        }
        nint rootDir = FdOpenDir(rootFd);
        if (rootDir == 0)
        {
            Close(rootFd);
            return false;
        }

        ulong device = rootStat.Dev;
        var frames = t_frames ??= new Frame[WalkState.MaxDepth + 1];
        int top = 0;
        frames[0] = new Frame { Dir = rootDir, Fd = rootFd, Node = 0, Depth = 0, Tag = IntermediateRule.None };

        var rule = state.Rule;
        var throttle = state.Throttle;
        var cancel = state.Cancellation;
        Span<char> name = stackalloc char[1024];
        Statx st;
        int batch = 0;
        long batchStart = Stopwatch.GetTimestamp();

        try
        {
            while (top >= 0)
            {
                ref Frame f = ref frames[top];
                byte* entry = ReadDir(f.Dir);
                if (entry == null)
                {
                    if (Marshal.GetLastPInvokeError() != 0)
                        state.Error();
                    CloseDir(f.Dir);
                    top--;
                    continue;
                }

                byte* raw = entry + DirentNameOffset;
                int length = 0;
                while (raw[length] != 0) length++;
                if (raw[0] == (byte)'.' && (length == 1 || (length == 2 && raw[1] == (byte)'.')))
                    continue;

                if (++batch == BatchSize)
                {
                    long now = Stopwatch.GetTimestamp();
                    throttle?.Pace(batch, Stopwatch.GetElapsedTime(batchStart, now), cancel);
                    cancel.ThrowIfCancellationRequested();
                    batch = 0;
                    batchStart = Stopwatch.GetTimestamp();
                }

                if (StatxAt(f.Fd, raw, AT_SYMLINK_NOFOLLOW | AT_NO_AUTOMOUNT | AT_STATX_DONT_SYNC, WalkMask, &st) != 0)
                {
                    // Removed since it was listed: not an error.
                    if (Marshal.GetLastPInvokeError() != ENOENT)
                        state.Error();
                    continue;
                }

                int chars = DecodeName(raw, length, name);
                var text = name[..chars];
                long allocated = (long)st.Blocks * 512;
                long mtime = st.MtimeTicks;
                int depth = f.Depth;

                switch (st.Type)
                {
                    case S_IFDIR:
                    {
                        int tag = FolderTag(rule, f.Tag, depth, text);
                        int node = state.Folder(f.Node, depth, text, tag, allocated, mtime);
                        if (st.Dev != device)
                            break; // another file system mounted inside: counted as one folder, not entered
                        if (depth + 1 >= WalkState.MaxDepth)
                        {
                            state.Error();
                            break;
                        }
                        int fd = OpenAt(f.Fd, raw, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
                        if (fd < 0)
                        {
                            if (Marshal.GetLastPInvokeError() != ENOENT)
                                state.Error();
                            break;
                        }
                        nint dir = FdOpenDir(fd);
                        if (dir == 0)
                        {
                            Close(fd);
                            state.Error();
                            break;
                        }
                        frames[++top] = new Frame { Dir = dir, Fd = fd, Node = node, Depth = depth + 1, Tag = tag };
                        break;
                    }
                    case S_IFREG:
                    {
                        int tag = FileTag(rule, f.Tag, depth, text);
                        if (st.NLink > 1 && !state.FirstHardLink(st.Dev, st.Ino))
                            state.ExtraHardLink(f.Node);
                        else
                            state.File(f.Node, tag, text, (long)st.Size, allocated, mtime);
                        break;
                    }
                    case S_IFLNK:
                        state.Link(f.Node, FileTag(rule, f.Tag, depth, text), allocated, mtime);
                        break;
                    default:
                        // Pipes, sockets, devices: an entry with no data.
                        state.File(f.Node, FileTag(rule, f.Tag, depth, text), ReadOnlySpan<char>.Empty, 0, allocated, mtime);
                        break;
                }
            }
        }
        finally
        {
            for (; top >= 0; top--)
                CloseDir(frames[top].Dir);
        }
        return true;
    }

    private static int FolderTag(IntermediateRule rule, int parentTag, int depth, ReadOnlySpan<char> name)
    {
        if (IntermediateRule.Covers(parentTag)) return parentTag;
        return rule?.FolderTag(parentTag, depth, name) ?? IntermediateRule.None;
    }

    private static int FileTag(IntermediateRule rule, int folderTag, int depth, ReadOnlySpan<char> name)
    {
        if (IntermediateRule.Covers(folderTag)) return folderTag;
        return rule?.FileTag(folderTag, depth, name) ?? IntermediateRule.None;
    }

    private static int DecodeName(byte* raw, int length, Span<char> into)
    {
        var bytes = new ReadOnlySpan<byte>(raw, length);
        // Names are at most 255 bytes, so 1024 chars always fit; invalid UTF-8 becomes U+FFFD.
        return Encoding.UTF8.GetChars(bytes, into);
    }
}

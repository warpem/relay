using System.Diagnostics;
using System.IO.Enumeration;

namespace Refund.Services.Storage.Walking;

/// <summary>
/// Portable walker on .NET's file system enumerator (macOS, Windows, other architectures). Links are counted, not
/// followed; hidden files are included. Allocated bytes equal file lengths, and hard links can't be told apart.
/// </summary>
internal static class ManagedWalker
{
    private const int BatchSize = 64;

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false,
    };

    private readonly record struct Pending(string Path, int Node, int Depth, int Tag);

    public static bool Walk(string root, WalkState state)
    {
        var info = new DirectoryInfo(root);
        if (!info.Exists || info.LinkTarget != null)
            return false;

        var pending = new Stack<Pending>();
        pending.Push(new(root, 0, 0, IntermediateRule.None));
        var batch = new Batch(state);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            using var entries = new Entries(folder, state, pending, batch);
            try
            {
                while (entries.MoveNext()) { }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                state.Error();
            }
        }
        return true;
    }

    private sealed class Batch(WalkState state)
    {
        private int _count;
        private long _start = Stopwatch.GetTimestamp();

        public void Tick()
        {
            if (++_count < BatchSize) return;
            state.Throttle?.Pace(_count, Stopwatch.GetElapsedTime(_start), state.Cancellation);
            state.Cancellation.ThrowIfCancellationRequested();
            _count = 0;
            _start = Stopwatch.GetTimestamp();
        }
    }

    private sealed class Entries(Pending folder, WalkState state, Stack<Pending> pending, Batch batch)
        : FileSystemEnumerator<byte>(folder.Path, Options)
    {
        private readonly WalkState _state = state;

        protected override bool ContinueOnError(int error)
        {
            _state.Error();
            return true;
        }

        protected override byte TransformEntry(ref FileSystemEntry entry)
        {
            batch.Tick();
            var rule = _state.Rule;
            var name = entry.FileName;
            var attributes = entry.Attributes;
            long mtime = entry.LastWriteTimeUtc.UtcTicks;
            int depth = folder.Depth;

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                _state.Link(folder.Node, FileTag(rule, folder.Tag, depth, name), 0, mtime);
            }
            else if (entry.IsDirectory)
            {
                int tag = IntermediateRule.Covers(folder.Tag) ? folder.Tag
                    : rule?.FolderTag(folder.Tag, depth, name) ?? IntermediateRule.None;
                int node = _state.Folder(folder.Node, depth, name, tag, 0, mtime);
                if (depth + 1 >= WalkState.MaxDepth)
                    _state.Error();
                else
                    pending.Push(new(entry.ToFullPath(), node, depth + 1, tag));
            }
            else
            {
                long length = entry.Length;
                _state.File(folder.Node, FileTag(rule, folder.Tag, depth, name), name, length, length, mtime);
            }
            return 0;
        }

        private static int FileTag(IntermediateRule rule, int folderTag, int depth, ReadOnlySpan<char> name) =>
            IntermediateRule.Covers(folderTag) ? folderTag : rule?.FileTag(folderTag, depth, name) ?? IntermediateRule.None;
    }
}

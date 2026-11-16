using Refund.Utils;

namespace Refund.Services.Storage;

/// <summary>Finds and removes a job's intermediate files with the same tags the walker measures them by.</summary>
internal static class IntermediateCleaner
{
    /// <summary>A tagged file or folder (a folder goes as a whole).</summary>
    public readonly record struct Candidate(string Path, bool IsFolder, int Tag);

    /// <summary>
    /// Lists the tagged entries of <paramref name="jobFolder"/>, entering only the folders the rule looks inside, and
    /// the iterations found on disk.
    /// </summary>
    public static (List<Candidate> Candidates, HashSet<int> OnDisk) Find(string jobFolder, IntermediateRule rule)
    {
        var candidates = new List<Candidate>();
        var onDisk = new HashSet<int>();
        if (!Directory.Exists(jobFolder))
            return (candidates, onDisk);

        var pending = new Stack<(string Path, int Depth, int Tag)>();
        pending.Push((jobFolder, 0, IntermediateRule.None));
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true, RecurseSubdirectories = false };
        while (pending.Count > 0)
        {
            var (folder, depth, folderTag) = pending.Pop();
            foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
            {
                bool isFolder = info is DirectoryInfo && info.LinkTarget == null;
                int tag = isFolder ? rule.FolderTag(folderTag, depth, info.Name) : rule.FileTag(folderTag, depth, info.Name);
                if (IntermediateRule.Covers(tag))
                {
                    candidates.Add(new(info.FullName, isFolder, tag));
                    if (tag >= 0) onDisk.Add(tag);
                }
                else if (isFolder && IntermediateRule.IsPrivate(tag))
                    pending.Push((info.FullName, depth + 1, tag));
            }
        }
        return (candidates, onDisk);
    }

    /// <summary>Removes the candidates whose iteration isn't kept, and everything regenerable. Returns how many went.</summary>
    public static int Delete(IEnumerable<Candidate> candidates, IReadOnlySet<int> keep, CancellationToken cancellation = default)
    {
        int removed = 0;
        foreach (var c in candidates)
        {
            cancellation.ThrowIfCancellationRequested();
            if (c.Tag >= 0 && keep.Contains(c.Tag))
                continue;
            if (c.IsFolder)
                FileUtils.DeleteDirectoryWithRetry(c.Path);
            else
                File.Delete(c.Path);
            removed++;
        }
        return removed;
    }
}

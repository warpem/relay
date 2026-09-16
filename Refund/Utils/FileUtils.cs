namespace Refund.Utils;

public static class FileUtils
{
    /// <summary>
    /// Deletes a directory recursively, allowing transient I/O conflicts to settle.
    /// Each attempt enumerates the remaining contents again. Persistent failures
    /// are propagated after a bounded retry period; permission errors fail immediately.
    /// </summary>
    internal static void DeleteDirectoryWithRetry(string path) =>
        DeleteDirectoryWithRetry(path, Directory.Delete, Thread.Sleep);

    internal static void DeleteDirectoryWithRetry(
        string path, Action<string, bool> deleteDirectory, Action<TimeSpan> delay)
    {
        const int maxAttempts = 6;

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                deleteDirectory(path, true);
                return;
            }
            catch (DirectoryNotFoundException) when (!Directory.Exists(path))
            {
                // Already removed, including by a concurrent cleanup.
                return;
            }
            catch (IOException) when (attempt < maxAttempts - 1)
            {
                // Wait 100, 200, 400, 800, then 1600 ms (3.1 seconds total).
                delay(TimeSpan.FromMilliseconds(100 * (1 << attempt)));
            }
        }
    }

    public static void CopyDirectoryContents(string sourceDir, string destDir, IEnumerable<string> excludedFiles = null, IEnumerable<string> excludeFolders = null)
    {
        if (excludedFiles == null)
            excludedFiles = Enumerable.Empty<string>();
        
        if (excludeFolders == null)
            excludeFolders = Enumerable.Empty<string>();

        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");

        if (!Directory.Exists(destDir))
            Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            if (excludedFiles.Any(f => string.Equals(f, 
                                                     Path.GetFileName(file), 
                                                     StringComparison.OrdinalIgnoreCase)))
                continue;

            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, true);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            if (excludeFolders.Any(f => string.Equals(f, 
                                                      Path.GetFileName(subDir), 
                                                      StringComparison.OrdinalIgnoreCase)))
                continue;
            
            var destSubDir = Path.Combine(destDir, Path.GetFileName(subDir));
            CopyDirectoryContents(subDir, destSubDir);
        }
    }
}

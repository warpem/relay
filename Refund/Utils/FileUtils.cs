namespace Refund.Utils;

public static class FileUtils
{
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
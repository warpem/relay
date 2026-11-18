namespace Refund.Services.Warp;

/// <summary>
/// Paths a Warp session saves relative to a root (the session's folder, or the space's), so they still hold after the
/// space moves. Paths outside the root, such as the raw movies, stay absolute: they don't move with the space.
/// </summary>
internal static class WarpPaths
{
    /// <summary><paramref name="path"/> relative to <paramref name="root"/> when it lies inside it; otherwise unchanged.</summary>
    public static string Relative(string root, string path)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path) || !Path.IsPathRooted(path)) return path;
        // Checkpoints map every movie's paths on each save, and these were made by combining the root with names.
        string trimmed = Path.TrimEndingDirectorySeparator(root);
        if (path.Length > trimmed.Length + 1 && path[trimmed.Length] == Path.DirectorySeparatorChar &&
            path.StartsWith(trimmed, StringComparison.Ordinal) && !path.Contains("..", StringComparison.Ordinal))
            return path[(trimmed.Length + 1)..];
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (full == rootFull) return ".";
        return full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full[(rootFull.Length + 1)..] : path;
    }

    /// <summary>A path saved by <see cref="Relative"/>, as an absolute path under <paramref name="root"/>; absolute paths stay as they are.</summary>
    public static string Resolve(string root, string path) =>
        string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path) || Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(root, path));
}

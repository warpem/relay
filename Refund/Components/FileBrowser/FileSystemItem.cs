namespace Refund.Components.FileBrowser;

/// <summary>
/// Represents a file system item (file or directory) in the FileBrowser component.
/// Provides metadata about the item including its path, name, type, size, and timestamps.
/// Implements lazy loading of expensive properties (size, creation time, etc.) for better performance.
/// Used extensively across the application for file system operations, previewing content,
/// and representing selectable items in components like FileBrowser and ThumbnailPanel.
/// </summary>
public class FileSystemItem
{
    /// <summary>
    /// Gets the full path to the file system item.
    /// Critical property used across components for file operations, including reading file content
    /// for previews (FilePreview), working with EM data files, and performing file system operations.
    /// </summary>
    public string Path { get; }
    
    /// <summary>
    /// Gets or sets the name of the file system item (filename or directory name).
    /// Used for display in UI, file filtering by extension patterns, and during rename operations.
    /// Set explicitly when creating virtual file items for operations like file saving.
    /// </summary>
    public string Name { get; set; }
    
    /// <summary>
    /// Gets or sets whether the item is a directory (true) or a file (false).
    /// Used extensively for sorting and filtering files vs. folders in FolderColumn, and to
    /// determine how to handle item selection in FileBrowser (navigate into folders vs. select files).
    /// Also used by FilePreview to determine whether to generate a preview (for files only).
    /// </summary>
    public bool IsDirectory { get; set; }
    
    /// <summary>
    /// Gets or sets whether the item is hidden in the file system.
    /// Used by FolderColumn to filter visible items based on the ShowHiddenItems setting,
    /// enabling users to toggle visibility of hidden files and folders.
    /// </summary>
    public bool IsHidden { get; set; }
    
    private bool _metadataLoaded;
    private bool _exists;
    private long _sizeInByte;
    private DateTime _creationTime;
    private DateTime _lastWriteTime;

    /// <summary>
    /// Whether the item still existed when its metadata was last read. A listing can outlive the
    /// files in it (e.g. scratch files of running jobs), so callers rendering metadata should check
    /// this instead of assuming the path is still there. Call <see cref="Refresh"/> to re-read.
    /// </summary>
    public bool Exists
    {
        get
        {
            LoadMetadata();
            return _exists;
        }
    }

    /// <summary>
    /// Gets the size of the item in bytes.
    /// For files, returns the actual file size; for directories and missing items, returns 0.
    /// Lazily loaded on first access for performance.
    /// </summary>
    public long SizeInByte
    {
        get
        {
            LoadMetadata();
            return _sizeInByte;
        }
    }

    /// <summary>
    /// Gets the creation time of the file system item, or <see cref="DateTime.MinValue"/> if it no longer exists.
    /// Lazily loaded on first access for performance.
    /// </summary>
    public DateTime CreationTime
    {
        get
        {
            LoadMetadata();
            return _creationTime;
        }
    }

    /// <summary>
    /// Gets the last write time of the file system item, or <see cref="DateTime.MinValue"/> if it no longer exists.
    /// Lazily loaded on first access for performance.
    /// </summary>
    public DateTime LastWriteTime
    {
        get
        {
            LoadMetadata();
            return _lastWriteTime;
        }
    }

    /// <summary>
    /// Discards cached metadata so the next access re-reads it from the file system.
    /// </summary>
    public void Refresh() => _metadataLoaded = false;

    /// <summary>
    /// Reads size and timestamps in a single stat. Never throws: an item that vanished or became
    /// unreadable since it was listed reports <see cref="Exists"/> = false with empty metadata.
    /// </summary>
    private void LoadMetadata()
    {
        if (_metadataLoaded)
            return;
        _metadataLoaded = true;

        _exists = false;
        _sizeInByte = 0;
        _creationTime = DateTime.MinValue;
        _lastWriteTime = DateTime.MinValue;

        try
        {
            FileSystemInfo info = IsDirectory ? new DirectoryInfo(Path) : new FileInfo(Path);
            if (!info.Exists)
                return;

            // Exists populated the info's cached stat, so these don't touch the disk again.
            _sizeInByte = info is FileInfo file ? file.Length : 0;
            _creationTime = info.CreationTime;
            _lastWriteTime = info.LastWriteTime;
            _exists = true;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            _exists = false;
            _sizeInByte = 0;
            _creationTime = DateTime.MinValue;
            _lastWriteTime = DateTime.MinValue;
        }
    }

    /// <summary>
    /// Initializes a new instance of the FileSystemItem class for a file or directory.
    /// </summary>
    /// <param name="path">The full path to the file system item</param>
    public FileSystemItem(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        IsDirectory = Directory.Exists(path);

        try
        {
            FileSystemInfo info = IsDirectory ? new DirectoryInfo(path) : new FileInfo(path);
            // Attributes reads as -1 (every flag set) for a missing item; don't hide it for that.
            IsHidden = info.Exists && info.Attributes.HasFlag(FileAttributes.Hidden);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            IsHidden = false;
        }
    }

    /// <summary>
    /// Gets the appropriate value for sorting based on the specified sort option.
    /// Used by FolderColumn to implement sorting of files and folders by different criteria.
    /// The returned value is used with LINQ OrderBy/OrderByDescending to sort collections.
    /// </summary>
    /// <param name="sortOption">The sort option: "Name", "Size", or "Date"</param>
    /// <returns>The value to use for sorting</returns>
    public object GetSortValue(string sortOption)
    {
        return sortOption switch
        {
            "Name" => Name,
            "Size" => SizeInByte,
            "Date" => LastWriteTime,
            _ => Name,
        };
    }

    /// <summary>
    /// Determines whether the specified object is equal to the current FileSystemItem.
    /// Two FileSystemItem objects are considered equal if they have the same Path.
    /// Used extensively in selection tracking across components like ThumbnailPanel
    /// to determine if items are currently selected and to maintain selection state.
    /// </summary>
    /// <param name="obj">The object to compare with the current object</param>
    /// <returns>true if the specified object is equal to the current object; otherwise, false</returns>
    public override bool Equals(object obj)
    {
        if (obj is FileSystemItem other)
            return Path == other.Path;

        return false;
    }
    
    /// <summary>
    /// Returns a hash code for this instance.
    /// Uses the Path property's hash code for consistency with Equals.
    /// Essential for dictionary operations and collections that use hash-based lookups,
    /// ensuring that equivalent FileSystemItems generate the same hash code.
    /// </summary>
    /// <returns>A hash code for the current FileSystemItem</returns>
    public override int GetHashCode()
    {
        return Path.GetHashCode();
    }
}
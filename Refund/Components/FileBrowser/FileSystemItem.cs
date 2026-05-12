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
    
    /// <summary>
    /// Cached file size to avoid repeated file system queries.
    /// </summary>
    private long? _sizeInByte;
    
    /// <summary>
    /// Gets the size of the item in bytes.
    /// For files, returns the actual file size.
    /// For directories, returns 0 (directory size calculation is not implemented).
    /// Lazily loaded on first access for performance.
    /// </summary>
    public long SizeInByte
    {
        get
        {
            if (_sizeInByte == null)
                _sizeInByte = !IsDirectory ? new FileInfo(Path).Length : 0;

            return _sizeInByte.Value;
        }
    }
    
    /// <summary>
    /// Cached creation time to avoid repeated file system queries.
    /// </summary>
    private DateTime? _creationTime;
    
    /// <summary>
    /// Gets the creation time of the file system item.
    /// Uses Directory.GetCreationTime for directories and File.GetCreationTime for files.
    /// Lazily loaded on first access for performance.
    /// </summary>
    public DateTime CreationTime
    {
        get
        {
            if (_creationTime == null)
                _creationTime = Directory.Exists(Path) ? Directory.GetCreationTime(Path) : File.GetCreationTime(Path);

            return _creationTime.Value;
        }
    }

    /// <summary>
    /// Cached last write time to avoid repeated file system queries.
    /// </summary>
    private DateTime? _lastWriteTime;
    
    /// <summary>
    /// Gets the last write time of the file system item.
    /// Uses Directory.GetLastWriteTime for directories and File.GetLastWriteTime for files.
    /// Lazily loaded on first access for performance.
    /// </summary>
    public DateTime LastWriteTime
    {
        get
        {
            if (_lastWriteTime == null)
                _lastWriteTime = Directory.Exists(Path) ? Directory.GetLastWriteTime(Path) : File.GetLastWriteTime(Path);
            
            return _lastWriteTime.Value;
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
        
        if (IsDirectory)
        {
            IsHidden = new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.Hidden);
        }
        else
        {
            IsHidden = new FileInfo(path).Attributes.HasFlag(FileAttributes.Hidden);
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
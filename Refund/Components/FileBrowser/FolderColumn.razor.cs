using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;
using NaturalSort.Extension;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Refund.Components.FileBrowser;

/// <summary>
/// Component that displays the contents of a directory as a column in the FileBrowser.
/// Shows folders and files with icons, and handles item selection.
/// Forms part of the column-based navigation UI where multiple columns represent the
/// folder hierarchy, enabling users to navigate through the file system.
/// </summary>
public partial class FolderColumn : ComponentBase
{
    /// <summary>
    /// The directory path for this column.
    /// </summary>
    [Parameter]
    public string Path { get; set; }
    
    /// <summary>
    /// The index of this column in the folder navigation hierarchy.
    /// </summary>
    [Parameter]
    public int LevelIndex { get; set; }
    
    /// <summary>
    /// Event callback that is triggered when an item in this column is selected.
    /// </summary>
    [Parameter]
    public EventCallback<ItemSelectedEventArgs> OnItemSelected { get; set; }
    
    /// <summary>
    /// The currently selected item in this column.
    /// </summary>
    [Parameter]
    public FileSystemItem SelectedItem { get; set; }
    
    /// <summary>
    /// The list of currently selected items across all columns.
    /// </summary>
    [Parameter]
    public List<FileSystemItem> SelectedItems { get; set; }
    
    /// <summary>
    /// Whether to show files in this column.
    /// </summary>
    [Parameter]
    public bool ShowFiles { get; set; }
    
    /// <summary>
    /// Whether files in this column should be selectable.
    /// When false, files are displayed but greyed out and non-interactive.
    /// </summary>
    [Parameter]
    public bool FilesAreSelectable { get; set; } = true;
    
    /// <summary>
    /// Whether to show folders in this column.
    /// </summary>
    [Parameter]
    public bool ShowFolders { get; set; }
    
    /// <summary>
    /// Whether to show hidden files and folders in this column.
    /// </summary>
    [Parameter]
    public bool ShowHiddenItems { get; set; }
    
    /// <summary>
    /// Array of allowed file extensions for filtering files in this column.
    /// </summary>
    [Parameter]
    public string[] AllowedExtensions { get; set; }
    
    /// <summary>
    /// The current sort option (Name, Size, or Date).
    /// </summary>
    [Parameter]
    public string SortOption { get; set; }
    
    /// <summary>
    /// Whether the sort order is ascending.
    /// </summary>
    [Parameter]
    public bool IsAscending { get; set; }
    
    /// <summary>
    /// Flag to force refreshing the column contents.
    /// </summary>
    [Parameter]
    public bool ForceRefresh { get; set; } = false;

    /// <summary>
    /// Icon for unselected folders.
    /// </summary>
    private static readonly Icon IconFolder = new Icons.Filled.Size16.Folder().WithColor(Color.Accent);
    
    /// <summary>
    /// Icon for unselected files.
    /// </summary>
    private static readonly Icon IconFile = new Icons.Regular.Size16.Document().WithColor(Color.Accent);
    
    /// <summary>
    /// Icon for selected folders.
    /// </summary>
    private static readonly Icon IconFolderSelected = new Icons.Filled.Size16.Folder().WithColor(Color.Fill);
    
    /// <summary>
    /// Icon for selected files.
    /// </summary>
    private static readonly Icon IconFileSelected = new Icons.Filled.Size16.Document().WithColor(Color.Fill);

    /// <summary>
    /// List of folders in the current directory.
    /// </summary>
    private List<FileSystemItem> Folders { get; set; }
    
    /// <summary>
    /// List of files in the current directory.
    /// </summary>
    private List<FileSystemItem> Files { get; set; }
    
    /// <summary>
    /// Flag indicating whether the column is currently loading its contents.
    /// </summary>
    private bool IsLoading { get; set; } = false;

    /// <summary>
    /// Called when component parameters are set or updated.
    /// Loads the directory contents if needed or if a refresh is forced.
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        if (ForceRefresh || Folders == null || Files == null)
        {
            await LoadItemsAsync();
            ForceRefresh = false;
        }
    }

    /// <summary>
    /// Loads the contents of the directory for this column.
    /// Sets the IsLoading flag to true during loading and updates the UI.
    /// </summary>
    private async Task LoadItemsAsync()
    {
        IsLoading = true;
        StateHasChanged();

        var items = await GetFolderContentsAsync(Path);

        Folders = items.Where(i => i.IsDirectory).ToList();
        Files = items.Where(i => !i.IsDirectory).ToList();

        IsLoading = false;
        StateHasChanged();
    }

    /// <summary>
    /// Asynchronously retrieves the contents of a directory.
    /// Filters items based on ShowFolders, ShowFiles, and AllowedExtensions parameters.
    /// Runs in a background thread to avoid blocking the UI.
    /// </summary>
    /// <param name="path">The directory path to get contents from</param>
    /// <returns>A list of FileSystemItem objects representing the directory contents</returns>
    private async Task<List<FileSystemItem>> GetFolderContentsAsync(string path)
    {
        return await Task.Run(() =>
        {
            var entries = Directory.EnumerateFileSystemEntries(path);
            var items = new List<FileSystemItem>();

            foreach (var entry in entries)
            {
                var isDirectory = Directory.Exists(entry);
                var item = new FileSystemItem(entry);

                if (isDirectory && ShowFolders)
                {
                    // Add directories if they should be shown
                    items.Add(item);
                }
                else if (!isDirectory && ShowFiles)
                {
                    // Add files if they should be shown and match the allowed extensions
                    if (AllowedExtensions.Length == 0 || AllowedExtensions.Any(ext => Regex.IsMatch(item.Name, WildcardToRegex(ext))))
                        items.Add(item);
                }
            }

            return items;
        });
    }

    /// <summary>
    /// Converts a wildcard pattern (like "*.txt") to a regular expression pattern.
    /// Used for matching file extensions.
    /// </summary>
    /// <param name="pattern">The wildcard pattern to convert</param>
    /// <returns>A regular expression pattern equivalent to the wildcard pattern</returns>
    private string WildcardToRegex(string pattern)
    {
        return "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
    }

    /// <summary>
    /// Handles the click event on an item in the column.
    /// Creates an event args object and invokes the OnItemSelected callback.
    /// Ignores clicks on non-clickable items.
    /// </summary>
    /// <param name="e">The mouse event arguments</param>
    /// <param name="item">The item that was clicked</param>
    private void OnItemClicked(MouseEventArgs e, FileSystemItem item)
    {
        if (!IsItemClickable(item))
            return;
            
        var args = new ItemSelectedEventArgs { Item = item, LevelIndex = LevelIndex, MouseEventArgs = e };
        OnItemSelected.InvokeAsync(args);
    }
    
    /// <summary>
    /// Determines whether an item should be clickable/interactive.
    /// Files are only clickable when FilesAreSelectable is true.
    /// Folders are always clickable.
    /// </summary>
    /// <param name="item">The item to check</param>
    /// <returns>True if the item should be clickable, false otherwise</returns>
    private bool IsItemClickable(FileSystemItem item)
    {
        return item.IsDirectory || FilesAreSelectable;
    }
    
    /// <summary>
    /// Gets CSS classes for an item based on its state.
    /// Non-selectable files are always greyed out and never selected.
    /// Only selectable items can have selected state.
    /// </summary>
    /// <param name="item">The item to get classes for</param>
    /// <returns>CSS class string</returns>
    private string GetItemCssClass(FileSystemItem item)
    {
        // Files in folder mode are always non-selectable, never selected
        if (!item.IsDirectory && !FilesAreSelectable)
            return "non-selectable";
            
        // Only selectable items can be selected
        if (IsSelected(item))
            return "selected";
            
        return "";
    }

    /// <summary>
    /// Determines whether an item is currently selected.
    /// An item is considered selected if it's in the SelectedItems list or matches the SelectedItem.
    /// </summary>
    /// <param name="item">The item to check</param>
    /// <returns>True if the item is selected, false otherwise</returns>
    private bool IsSelected(FileSystemItem item)
    {
        if (SelectedItems != null && SelectedItems.Contains(item))
            return true;

        if (SelectedItem != null && SelectedItem.Path == item.Path)
            return true;

        return false;
    }

    /// <summary>
    /// Gets the folders that should be visible in the column.
    /// Filters out hidden folders unless ShowHiddenItems is true.
    /// </summary>
    private IEnumerable<FileSystemItem> VisibleFolders => Folders.Where(item => ShowHiddenItems || !item.IsHidden);
    
    /// <summary>
    /// Gets the files that should be visible in the column.
    /// Filters out hidden files unless ShowHiddenItems is true.
    /// </summary>
    private IEnumerable<FileSystemItem> VisibleFiles => Files.Where(item => ShowHiddenItems || !item.IsHidden);

    /// <summary>
    /// Gets the sorted list of visible folders.
    /// Sorts by the specified SortOption in ascending or descending order.
    /// Uses natural sorting for names when the list is small enough (≤2000 items) for good performance.
    /// </summary>
    private IEnumerable<FileSystemItem> SortedFolders
    {
        get
        {
            var folders = VisibleFolders;
            var useNaturalSort = SortOption == "Name" && folders.Count() <= 2000;
            
            if (useNaturalSort)
                return IsAscending 
                    ? folders.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase.WithNaturalSort())
                    : folders.OrderByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase.WithNaturalSort());
            else
                return IsAscending
                    ? folders.OrderBy(item => item.GetSortValue(SortOption))
                    : folders.OrderByDescending(item => item.GetSortValue(SortOption));
        }
    }

    /// <summary>
    /// Gets the sorted list of visible files.
    /// Sorts by the specified SortOption in ascending or descending order.
    /// Uses natural sorting for names when the list is small enough (≤2000 items) for good performance.
    /// </summary>
    private IEnumerable<FileSystemItem> SortedFiles
    {
        get
        {
            var files = VisibleFiles;
            var useNaturalSort = SortOption == "Name" && files.Count() <= 2000;
            
            if (useNaturalSort)
                return IsAscending 
                    ? files.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase.WithNaturalSort())
                    : files.OrderByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase.WithNaturalSort());
            else
                return IsAscending
                    ? files.OrderBy(item => item.GetSortValue(SortOption))
                    : files.OrderByDescending(item => item.GetSortValue(SortOption));
        }
    }
}

/// <summary>
/// Event arguments for when an item is selected in a folder column.
/// Contains information about the selected item, the column level, and the mouse event.
/// Used by FileBrowser to track and manage item selection across columns, 
/// enabling proper navigation, multi-selection, and context-aware actions.
/// </summary>
public class ItemSelectedEventArgs
{
    /// <summary>
    /// The file system item that was selected.
    /// </summary>
    public FileSystemItem Item { get; set; }
    
    /// <summary>
    /// The index of the folder level (column) where the item was selected.
    /// </summary>
    public int LevelIndex { get; set; }
    
    /// <summary>
    /// The mouse event arguments from the click event.
    /// Contains information about mouse button, modifier keys, etc.
    /// Used for implementing multi-selection with modifier keys (Ctrl/Shift)
    /// and contextual selection behavior in FileBrowser.
    /// </summary>
    public MouseEventArgs MouseEventArgs { get; set; }
}

/// <summary>
/// Represents a level (column) in the folder navigation hierarchy.
/// Tracks the path, selected item, and refresh state for a specific folder level.
/// Used by FileBrowser to maintain state for each column in the navigation interface,
/// allowing for dynamic updates, refreshing, and navigation through the folder hierarchy.
/// </summary>
public class FolderLevel
{
    /// <summary>
    /// The directory path for this folder level.
    /// </summary>
    public string Path { get; set; }
    
    /// <summary>
    /// The currently selected item in this folder level.
    /// </summary>
    public FileSystemItem SelectedItem { get; set; }
    
    /// <summary>
    /// Flag indicating whether this folder level should be forcibly refreshed.
    /// When set to true, triggers a reload of directory contents regardless of cache state.
    /// Used to ensure up-to-date content after file system operations like creating, 
    /// deleting, or uploading files.
    /// </summary>
    public bool ForceRefresh { get; set; } = false;
    
    /// <summary>
    /// Counter that increments to trigger a refresh of the folder level.
    /// Changing this property causes the folder level to refresh its contents.
    /// Used as a mechanism to trigger state changes in parent components
    /// for refreshing after file operations without full component reconstruction.
    /// </summary>
    public int RefreshTrigger { get; set; } = 0;
}

/// <summary>
/// Defines the selection modes available in the file browser.
/// Used to configure FileBrowser's selection behavior for different use cases
/// such as opening files, selecting directories, or saving files.
/// </summary>
public enum SelectionMode
{
    /// <summary>
    /// Allows selection of a single file only.
    /// </summary>
    SingleFile,
    
    /// <summary>
    /// Allows selection of multiple files.
    /// </summary>
    MultipleFiles,
    
    /// <summary>
    /// Allows selection of a single folder only.
    /// </summary>
    SingleFolder,
    
    /// <summary>
    /// Used for save file dialogs, allows specifying a filename to save to.
    /// </summary>
    SaveFile
}
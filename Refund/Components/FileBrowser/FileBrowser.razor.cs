using System.Linq.Expressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Refund.Components.FileBrowser;

/// <summary>
/// A file browser component that allows users to navigate, select, and perform operations on files and directories.
/// Supports different selection modes, including single file, multiple files, single folder, and saving files.
/// </summary>
public partial class FileBrowser : ComponentBase, IAsyncDisposable
{
    [Inject]
    private IJSRuntime JSRuntime { get; set; }
    
    [Inject]
    private ILogger<FileBrowser> Logger { get; set; } = default!;

    #region Parameters

    /// <summary>
    /// Event callback that is triggered when the user confirms a selection.
    /// Returns the list of selected <see cref="FileSystemItem"/> objects.
    /// </summary>
    [Parameter]
    public EventCallback<List<FileSystemItem>> OnSelectionConfirmed { get; set; }

    /// <summary>
    /// Event callback that is triggered when the user cancels the selection process.
    /// </summary>
    [Parameter]
    public EventCallback OnSelectionCanceled { get; set; }

    /// <summary>
    /// Specifies the mode of selection for the file browser.
    /// Can be set to select a single file, multiple files, a single folder, or save a file.
    /// Default is <see cref="SelectionMode.SingleFile"/>.
    /// </summary>
    [Parameter]
    public SelectionMode SelectionMode { get; set; } = SelectionMode.SingleFile;

    /// <summary>
    /// Determines whether files are displayed in the browser.
    /// Set to false to show only folders. Default is true.
    /// </summary>
    [Parameter]
    public bool ShowFiles { get; set; } = true;

    /// <summary>
    /// Determines whether folders are displayed in the browser.
    /// Set to false to show only files. Default is true.
    /// </summary>
    [Parameter]
    public bool ShowFolders { get; set; } = true;

    /// <summary>
    /// Array of file extensions that will be allowed for selection.
    /// For example: new[] { "*.txt", "*.csv" }. Default is an empty array (all extensions allowed).
    /// </summary>
    [Parameter]
    public string[] AllowedExtensions { get; set; } = Array.Empty<string>();

    /// <summary>
    /// The root folder from which browsing will start.
    /// Navigation cannot go above this folder. Default is "/".
    /// </summary>
    [Parameter]
    public string TopLevelFolder { get; set; } = "/";

    private string _CurrentFolder = null;
    
    /// <summary>
    /// The currently active folder in the browser.
    /// This property can be bound to track or control the current navigation path.
    /// When this property changes, the browser will navigate to the specified folder.
    /// </summary>
    [Parameter]
    public string CurrentFolder
    {
        get => _CurrentFolder;
        set
        {
            if (value != null && _CurrentFolder != value)
            {
                _CurrentFolder = value;
                _ = OnCurrentFolderChanged(value);
                // Check if the new folder is writable
                IsCurrentFolderWritable = IsDirectoryWritable(value);
            }
        }
    }
    
    /// <summary>
    /// Internal method that handles the current folder change event.
    /// Invokes the CurrentFolderChanged event callback and notifies the EditContext if available.
    /// </summary>
    /// <param name="value">The new folder path</param>
    private async Task OnCurrentFolderChanged(string value)
    {
        if (CurrentFolderChanged.HasDelegate)
            await CurrentFolderChanged.InvokeAsync(value);

        if (_hasEditContext)
        {
            EditContext.NotifyFieldChanged(_fieldIdentifier);
        }
    }

    /// <summary>
    /// Event callback that is triggered when the current folder changes.
    /// </summary>
    [Parameter]
    public EventCallback<string> CurrentFolderChanged { get; set; }
    
    /// <summary>
    /// Expression that identifies the current folder property for two-way binding.
    /// </summary>
    [Parameter]
    public Expression<Func<string>> CurrentFolderExpression { get; set; }
    
    /// <summary>
    /// Edit context from a cascading parameter, used for integrating with form validation.
    /// </summary>
    [CascadingParameter] 
    private EditContext EditContext { get; set; }
    
    /// <summary>
    /// Field identifier for the CurrentFolder property in the EditContext.
    /// </summary>
    private FieldIdentifier _fieldIdentifier;
    
    /// <summary>
    /// Flag indicating whether an EditContext is available.
    /// </summary>
    private bool _hasEditContext;

    /// <summary>
    /// Determines whether the Select/Save buttons are displayed.
    /// Set to false to hide the selection confirmation buttons. Default is true.
    /// </summary>
    [Parameter]
    public bool ShowSelectionButtons { get; set; } = true;

    /// <summary>
    /// Determines whether the Cancel button is displayed.
    /// Set to false to hide the cancel button. Default is true.
    /// </summary>
    [Parameter]
    public bool ShowCancelButton { get; set; } = true;

    /// <summary>
    /// The height of the file browser component in pixels.
    /// Default is 600 pixels.
    /// </summary>
    [Parameter]
    public int Height { get; set; } = 600;

    #endregion

    #region State

    /// <summary>
    /// List of folder levels being displayed in the browser.
    /// Each level represents a folder in the path hierarchy from the top level to the current folder.
    /// </summary>
    private List<FolderLevel> FolderLevels { get; set; } = new();
    
    /// <summary>
    /// List of currently selected items in the browser.
    /// </summary>
    private List<FileSystemItem> SelectedItems { get; set; } = new();
    
    /// <summary>
    /// Flag indicating whether the delete confirmation dialog is visible.
    /// </summary>
    private bool IsDeleteConfirmationVisible { get; set; } = false;
    
    /// <summary>
    /// Flag indicating whether the rename modal dialog is visible.
    /// </summary>
    private bool IsRenameModalVisible { get; set; } = false;
    
    /// <summary>
    /// The new name to be applied during a rename operation.
    /// </summary>
    private string NewName { get; set; }
    
    /// <summary>
    /// Flag indicating whether the current folder is writable.
    /// This affects which file operations are available to the user.
    /// </summary>
    private bool IsCurrentFolderWritable { get; set; } = true;

    #endregion

    #region Directory Permissions

    /// <summary>
    /// Checks if a directory is writable by attempting to create and delete a temporary file.
    /// This provides a reliable cross-platform way to detect write permissions.
    /// </summary>
    /// <param name="path">Directory path to check</param>
    /// <returns>True if the directory is writable, false otherwise</returns>
    private bool IsDirectoryWritable(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Logger.LogWarning("IsDirectoryWritable called with null or empty path");
            return false;
        }

        try
        {
            // Normalize the path to handle any path issues
            path = Path.GetFullPath(path);
            
            // If the directory doesn't exist, we can't write to it
            if (!Directory.Exists(path))
            {
                Logger.LogDebug("Directory does not exist: {Path}", path);
                return false;
            }

            // Try creating a temporary file with a more unique name to avoid conflicts
            var testFileName = $".relay_write_test_{Environment.ProcessId}_{Guid.NewGuid():N}.tmp";
            var testFilePath = Path.Combine(path, testFileName);
            
            // Attempt to create and immediately delete a test file
            // Use a more targeted approach with specific exception handling
            using (var fs = File.Create(testFilePath, 1, FileOptions.DeleteOnClose))
            {
                // Write a single byte to ensure we can actually write data
                fs.WriteByte(0x42);
                fs.Flush();
            }
            
            // File should be automatically deleted due to DeleteOnClose, but double-check
            if (File.Exists(testFilePath))
            {
                try
                {
                    File.Delete(testFilePath);
                }
                catch (Exception cleanupEx)
                {
                    Logger.LogWarning(cleanupEx, "Failed to cleanup test file: {TestFilePath}", testFilePath);
                    // Don't fail the check just because cleanup failed
                }
            }
            
            Logger.LogDebug("Directory write test succeeded: {Path}", path);
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.LogDebug(ex, "Directory not writable due to access restrictions: {Path}", path);
            return false;
        }
        catch (DirectoryNotFoundException ex)
        {
            Logger.LogDebug(ex, "Directory not found during write test: {Path}", path);
            return false;
        }
        catch (PathTooLongException ex)
        {
            Logger.LogDebug(ex, "Path too long for write test: {Path}", path);
            return false;
        }
        catch (IOException ex) when (ex.HResult == unchecked((int)0x80070020)) // ERROR_SHARING_VIOLATION
        {
            Logger.LogDebug(ex, "Sharing violation during write test (file may be locked): {Path}", path);
            return false;
        }
        catch (IOException ex)
        {
            Logger.LogWarning(ex, "IO error during directory write test, assuming not writable: {Path}", path);
            return false;
        }
        catch (Exception ex)
        {
            // For any other unexpected exception, log it but assume directory is not writable
            // This is safer than allowing potentially dangerous operations
            Logger.LogWarning(ex, "Unexpected error during directory write test, assuming not writable: {Path}", path);
            return false;
        }
    }

    #endregion

    #region Address Bar

    /// <summary>
    /// The current editable address shown in the address bar.
    /// This is a relative path from the top level folder.
    /// </summary>
    private string EditableAddress { get; set; }

    /// <summary>
    /// Indicates whether the top level folder is not a root directory.
    /// Used to determine if the parent navigation button should be enabled.
    /// </summary>
    private bool IsTopLevelFolderNotRoot
    {
        get
        {
            if (string.IsNullOrEmpty(TopLevelFolder))
                return false;

            return TopLevelFolder != Path.GetPathRoot(TopLevelFolder);
        }
    }

    /// <summary>
    /// Gets the display label for the top level folder with appropriate directory separator.
    /// </summary>
    private string TopLevelFolderLabel
    {
        get
        {
            if (string.IsNullOrEmpty(TopLevelFolder))
                return "/";

            return TopLevelFolder.EndsWith(Path.DirectorySeparatorChar)
                       ? TopLevelFolder
                       : TopLevelFolder + Path.DirectorySeparatorChar;
        }
    }

    /// <summary>
    /// Handles key events in the address bar, typically when the user presses Enter.
    /// </summary>
    /// <param name="value">The address text entered by the user</param>
    private async Task HandleAddressBarKeyDown(string value)
    {
        EditableAddress = value;
        await NavigateToAddressAsync();
    }

    /// <summary>
    /// Attempts to navigate to the address specified in the address bar.
    /// Validates the path and ensures it exists and is within the allowed navigation scope.
    /// </summary>
    private async Task NavigateToAddressAsync()
    {
        var newPath = EditableAddress;

        // If the path is not rooted, combine it with the top level folder
        if (!Path.IsPathRooted(newPath))
            newPath = string.IsNullOrEmpty(TopLevelFolder) ? newPath : Path.Combine(TopLevelFolder, newPath);

        newPath = Path.GetFullPath(newPath);

        // Check if the path is accessible and exists
        if (CanNavigateTo(newPath) && Directory.Exists(newPath))
        {
            CurrentFolder = newPath;
            await InitializeFolderLevelsAsync();
        }
        else
        {
            // Invalid path; revert to current folder
            EditableAddress = GetRelativePath(CurrentFolder);
        }

        StateHasChanged();
    }

    /// <summary>
    /// Converts an absolute file path to a relative path from the top level folder.
    /// This is used for displaying paths in the address bar.
    /// </summary>
    /// <param name="fullPath">The absolute path to convert</param>
    /// <returns>A relative path from the top level folder, or an empty string if at the top level</returns>
    private string GetRelativePath(string fullPath)
    {
        if (string.IsNullOrEmpty(TopLevelFolder))
            return fullPath;
        
        if (string.IsNullOrEmpty(fullPath))
            return "";

        var topFullPath = Path.GetFullPath(TopLevelFolder);
        var relativePath = Path.GetRelativePath(topFullPath, fullPath);

        return relativePath == "." ? "" : relativePath;
    }

    #endregion

    #region Sorting

    /// <summary>
    /// List of available sorting options for file system items.
    /// </summary>
    private static List<Option<string>> SortOptions { get; } = new()
    {
        new() { Value = "Name", Text = "Name" },
        new() { Value = "Size", Text = "Size" },
        new() { Value = "Date", Text = "Date" }
    };

    /// <summary>
    /// The currently selected sort option.
    /// </summary>
    private Option<string> SelectedSortOption = SortOptions.First();
    
    /// <summary>
    /// The current sort option value.
    /// </summary>
    private string SortOption => SelectedSortOption.Value;
    
    /// <summary>
    /// Flag indicating whether the sort order is ascending.
    /// </summary>
    private bool SortIsAscending { get; set; } = true;

    /// <summary>
    /// Icon representing ascending sort order.
    /// </summary>
    private Icon IconSortAscending = new Icons.Regular.Size20.ArrowSortUpLines();
    
    /// <summary>
    /// Icon representing descending sort order.
    /// </summary>
    private Icon IconSortDescending = new Icons.Regular.Size20.ArrowSortDownLines();
    
    /// <summary>
    /// Gets the appropriate sort icon based on the current sort direction.
    /// </summary>
    private Icon IconSort => SortIsAscending ? IconSortAscending : IconSortDescending;

    /// <summary>
    /// Toggles the sort direction between ascending and descending.
    /// </summary>
    private void ToggleSortDirection()
    {
        SortIsAscending = !SortIsAscending;
    }

    /// <summary>
    /// Handles changes to the selected sort option.
    /// </summary>
    /// <param name="e">The newly selected sort option</param>
    private void OnSortOptionChanged(Option<string> e)
    {
        SelectedSortOption = e;
    }

    #endregion

    #region Icons and Flags

    /// <summary>
    /// Flag indicating whether hidden files should be displayed.
    /// </summary>
    private bool ShowHiddenFiles { get; set; } = false;
    
    /// <summary>
    /// Icon for the show/hide hidden files toggle button.
    /// </summary>
    private Icon IconShowHiddenFiles = new Icons.Regular.Size20.Eye();

    /// <summary>
    /// Icon for the create folder button.
    /// </summary>
    private readonly Icon IconCreateFolder = new Icons.Regular.Size24.FolderAdd();
    
    /// <summary>
    /// Icon for the upload file button.
    /// </summary>
    private readonly Icon IconUploadFile = new Icons.Regular.Size20.DocumentArrowUp();
    
    /// <summary>
    /// Icon for the rename button.
    /// </summary>
    private readonly Icon IconRename = new Icons.Regular.Size24.Rename();
    
    /// <summary>
    /// Icon for the delete button.
    /// </summary>
    private readonly Icon IconDelete = new Icons.Regular.Size24.Delete();

    /// <summary>
    /// Reference to the columns container element for JavaScript interop.
    /// </summary>
    private ElementReference columnsContainer;

    /// <summary>
    /// JavaScript module reference for interop.
    /// </summary>
    private IJSObjectReference _module;

    #endregion

    #region Lifecycle Methods

    /// <summary>
    /// Initializes the component when it is first created.
    /// Sets up default values, initializes the current folder, and checks permissions.
    /// </summary>
    protected override async Task OnInitializedAsync()
    {
        // Initialize TopLevelFolder if not provided
        if (string.IsNullOrEmpty(TopLevelFolder))
            TopLevelFolder = Path.GetPathRoot(Environment.CurrentDirectory);

        // Initialize CurrentFolder if not provided
        if (string.IsNullOrEmpty(CurrentFolder))
            CurrentFolder = TopLevelFolder;

        // Initialize the address bar
        EditableAddress = GetRelativePath(CurrentFolder);

        // Initialize default save filename if in SaveFile mode
        if (SelectionMode == SelectionMode.SaveFile && AllowedExtensions.Length > 0)
            SaveFileName = $"new_file{AllowedExtensions[0].Replace("*", "")}";

        // Check if the initial folder is writable
        IsCurrentFolderWritable = IsDirectoryWritable(CurrentFolder);

        // Set up the folder hierarchy display
        await InitializeFolderLevelsAsync();
    }

    /// <summary>
    /// Executed after the component has been rendered.
    /// Handles JavaScript module initialization and scrolling.
    /// </summary>
    /// <param name="firstRender">Indicates whether this is the first time the component has been rendered</param>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // Scroll each column to reveal its selected item (e.g. when opened at a predefined path)
            await ScrollSelectedIntoViewAsync();
        }
        else
        {
            // Scroll to show the rightmost column
            await ScrollToRightAsync();
        }
    }
    
    /// <summary>
    /// Executed when the component's parameters are set.
    /// Sets up the EditContext for form integration if needed.
    /// </summary>
    protected override void OnParametersSet()
    {
        base.OnParametersSet();
    
        // Initialize the field identifier for form integration if needed
        if (CurrentFolderExpression != null && 
            !_hasEditContext)
        {
            _hasEditContext = EditContext != null;
            if (_hasEditContext)
                _fieldIdentifier = FieldIdentifier.Create(CurrentFolderExpression);
        }
    }

    #endregion

    #region Initialization Methods

    /// <summary>
    /// Initializes the folder hierarchy display.
    /// This method sets up the FolderLevels collection which represents the path from the top level folder
    /// to the current folder, with each level being a column in the file browser.
    /// </summary>
    private async Task InitializeFolderLevelsAsync()
    {
        // Ensure TopLevelFolder is set
        if (string.IsNullOrEmpty(TopLevelFolder))
            TopLevelFolder = Path.GetPathRoot(Environment.CurrentDirectory);

        // Ensure CurrentFolder is set and is within navigation boundaries
        if (string.IsNullOrEmpty(CurrentFolder))
            CurrentFolder = TopLevelFolder;
        else if (!CanNavigateTo(CurrentFolder))
            CurrentFolder = TopLevelFolder;

        // Check if the current folder is writable
        IsCurrentFolderWritable = IsDirectoryWritable(CurrentFolder);

        // Build the list of folder paths from TopLevelFolder to CurrentFolder
        var paths = GetFolderPathsFromTopToCurrent();

        // Initialize FolderLevels
        FolderLevels.Clear();

        for (int i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            var folderLevel = new FolderLevel { Path = path };

            // If there's a next level, set SelectedItem to the folder leading to it
            if (i + 1 < paths.Count)
            {
                var nextPath = paths[i + 1];
                folderLevel.SelectedItem = new FileSystemItem(nextPath);
            }

            FolderLevels.Add(folderLevel);
        }

        // Clear selection
        SelectedItems.Clear();

        // Update the address bar
        EditableAddress = GetRelativePath(CurrentFolder);

        StateHasChanged();
    }

    /// <summary>
    /// Builds a list of folder paths from the top level folder to the current folder.
    /// This represents the navigation hierarchy that will be displayed in the browser.
    /// </summary>
    /// <returns>A list of folder paths, starting with the top level folder and ending with the current folder</returns>
    private List<string> GetFolderPathsFromTopToCurrent()
    {
        var paths = new List<string>();
        var topFullPath = Path.GetFullPath(TopLevelFolder);
        var currentFullPath = Path.GetFullPath(CurrentFolder);

        // Make sure the current folder is within the top level folder
        if (!currentFullPath.StartsWith(topFullPath, StringComparison.OrdinalIgnoreCase))
            currentFullPath = topFullPath;

        // Get the relative path from top level to current
        var relativePath = Path.GetRelativePath(topFullPath, currentFullPath);
        var pathSegments = relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        // Start with the top level folder
        var cumulativePath = topFullPath;
        paths.Add(cumulativePath);

        // Build the path segments cumulatively
        foreach (var segment in pathSegments)
        {
            if (segment == ".")
                continue;

            cumulativePath = Path.Combine(cumulativePath, segment);
            paths.Add(cumulativePath);
        }

        return paths;
    }

    #endregion

    #region Event Handlers

    /// <summary>
    /// Handles the selection of an item in a folder level.
    /// If the selected item is a directory, the browser will navigate into that directory.
    /// If it's a file, it will be selected according to the current selection mode.
    /// </summary>
    /// <param name="args">Event arguments containing the selected item and level index</param>
    private async Task OnItemSelectedAsync(ItemSelectedEventArgs args)
    {
        var item = args.Item;
        var levelIndex = args.LevelIndex;

        // Update the selected item at the current level
        FolderLevels[levelIndex].SelectedItem = item;

        // Remove any levels beyond this one
        if (FolderLevels.Count > levelIndex + 1)
        {
            FolderLevels.RemoveRange(levelIndex + 1, FolderLevels.Count - levelIndex - 1);

            // Since the user navigated back to a parent folder, clear the selection
            SelectedItems.Clear();
        }

        if (item.IsDirectory)
        {
            // Navigate into the folder
            // Clear selection when a folder is selected
            SelectedItems.Clear();

            // Add a new level for the selected folder
            var newFolderLevel = new FolderLevel { Path = item.Path };
            FolderLevels.Add(newFolderLevel);
            CurrentFolder = item.Path;

            // Update the address bar
            EditableAddress = GetRelativePath(CurrentFolder);
        }
        else
        {
            // Clear selection of folders if a file is selected at the same level
            if (SelectedItems.Any() && SelectedItems.First().IsDirectory)
            {
                SelectedItems.Clear();
            }

            // Update selected items
            ToggleSelection(item, args.MouseEventArgs);
        }

        StateHasChanged();
    }

    /// <summary>
    /// Toggles the selection state of an item based on the current selection mode and modifier keys.
    /// Handles single selection, multi-selection with Ctrl/Cmd key, and folder-only selection.
    /// </summary>
    /// <param name="item">The item to toggle selection for</param>
    /// <param name="e">Mouse event arguments containing modifier key states</param>
    private void ToggleSelection(FileSystemItem item, MouseEventArgs e)
    {
        // Check if item is appropriate for selection mode
        switch (SelectionMode)
        {
            case SelectionMode.SingleFile:
                if (item.IsDirectory)
                    return; // Ignore directories
                SelectedItems.Clear();
                SelectedItems.Add(item);
                break;

            case SelectionMode.MultipleFiles:
                if (item.IsDirectory)
                    return; // Ignore directories
                bool isCtrlPressed = e.CtrlKey || e.MetaKey; // MetaKey for Cmd on Mac
                if (isCtrlPressed)
                {
                    // Toggle selection when Ctrl/Cmd is pressed
                    if (SelectedItems.Contains(item))
                        SelectedItems.Remove(item);
                    else
                        SelectedItems.Add(item);
                }
                else
                {
                    // Replace selection when Ctrl/Cmd is not pressed
                    SelectedItems.Clear();
                    SelectedItems.Add(item);
                }
                break;

            case SelectionMode.SingleFolder:
                // In SingleFolder mode, selection is handled via the Select button
                break;
        }
    }

    /// <summary>
    /// Scrolls the columns container to show the rightmost column.
    /// This ensures that the most recently opened folder is visible.
    /// </summary>
    private async Task ScrollToRightAsync()
    {
        if (_module == null)
            _module = await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./_content/Refund/Components/FileBrowser/FileBrowser.razor.js");
        await _module.InvokeVoidAsync("scrollToRight", columnsContainer);
    }

    /// <summary>
    /// Observes columns for selected items and scrolls each column to center on its selection.
    /// Uses a MutationObserver to handle columns that load asynchronously.
    /// Called on first render so that opening the browser at a predefined path
    /// auto-scrolls each column to its highlighted folder.
    /// </summary>
    private async Task ScrollSelectedIntoViewAsync()
    {
        if (_module == null)
            _module = await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./_content/Refund/Components/FileBrowser/FileBrowser.razor.js");
        await _module.InvokeVoidAsync("observeAndScrollSelected", columnsContainer);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Determines whether navigation to a specified path is allowed.
    /// Navigation is restricted to paths within the top level folder.
    /// </summary>
    /// <param name="path">The path to check</param>
    /// <returns>True if navigation is allowed, false otherwise</returns>
    private bool CanNavigateTo(string path)
    {
        if (string.IsNullOrEmpty(TopLevelFolder))
            return true; // No restriction if top level folder is not set

        var fullPath = Path.GetFullPath(path);
        var topFullPath = Path.GetFullPath(TopLevelFolder);

        // Check if the path is within the top level folder
        return fullPath.StartsWith(topFullPath, StringComparison.OrdinalIgnoreCase);
    }
    
    /// <summary>
    /// Determines whether files should be displayed in the browser.
    /// Files are hidden in single folder selection mode or when ShowFiles is false.
    /// </summary>
    private bool ShowFilesInBrowser => SelectionMode != SelectionMode.SingleFolder && ShowFiles;

    #endregion

    #region File Operations

    #region Create folder
    
    /// <summary>
    /// Flag indicating whether the create folder modal dialog is visible.
    /// </summary>
    private bool IsCreateFolderModalVisible { get; set; } = false;
    
    /// <summary>
    /// The name for the new folder being created.
    /// </summary>
    private string NewFolderName { get; set; }

    /// <summary>
    /// Initiates the folder creation process by showing the create folder dialog.
    /// Sets a default unique folder name.
    /// </summary>
    private async void CreateFolderAsync()
    {
        NewFolderName = GetUniqueFolderName(CurrentFolder, "New Folder");
        IsCreateFolderModalVisible = true;
    }

    /// <summary>
    /// Creates a new folder with the specified name when the user confirms the creation.
    /// Ensures the folder name is unique and refreshes the current view.
    /// </summary>
    private async Task ConfirmCreateFolderAsync()
    {
        if (!string.IsNullOrWhiteSpace(NewFolderName))
        {
            var currentPath = CurrentFolder;
            var newFolderName = GetUniqueFolderName(currentPath, NewFolderName);
            var newFolderPath = Path.Combine(currentPath, newFolderName);
            Directory.CreateDirectory(newFolderPath);

            // Refresh the current folder level
            var currentLevel = FolderLevels.LastOrDefault();

            if (currentLevel != null)
            {
                currentLevel.ForceRefresh = true;
                currentLevel.RefreshTrigger++;
            }

            IsCreateFolderModalVisible = false;
            NewFolderName = string.Empty;

            StateHasChanged();
        }
    }

    /// <summary>
    /// Generates a unique folder name by appending a number if necessary.
    /// This prevents naming conflicts when creating new folders.
    /// </summary>
    /// <param name="path">The directory path where the folder will be created</param>
    /// <param name="baseName">The base name for the folder</param>
    /// <returns>A unique folder name that doesn't conflict with existing items</returns>
    private string GetUniqueFolderName(string path, string baseName)
    {
        var name = baseName;
        var counter = 1;

        // Keep incrementing the counter until a unique name is found
        while (Directory.Exists(Path.Combine(path, name)) || File.Exists(Path.Combine(path, name)))
        {
            name = $"{baseName} ({counter++})";
        }

        return name;
    }
    
    #endregion
    
    #region Upload

    /// <summary>
    /// Reference to the file upload input component.
    /// </summary>
    private FluentInputFile? fileUploader;
    
    /// <summary>
    /// The folder path where files will be uploaded to.
    /// </summary>
    private string uploadLocation;
    
    /// <summary>
    /// Service for displaying toast notifications.
    /// </summary>
    [Inject]
    IToastService ToastService { get; set; }
    
    /// <summary>
    /// Dictionary tracking files currently being uploaded and their toast notifications.
    /// The key is the file name, and the value is the toast parameters for the upload progress.
    /// </summary>
    private Dictionary<string, ToastParameters<ProgressToastContent>> filesBeingUploaded = new();
    
    /// <summary>
    /// Creates and displays a toast notification for a file being uploaded.
    /// </summary>
    /// <param name="fileName">The name of the file being uploaded</param>
    private void AddUploadToast(string fileName)
    {
        if (filesBeingUploaded.ContainsKey(fileName))
            return;
        
        var toast = new ToastParameters<ProgressToastContent>
        {
            Id = fileName,
            Intent = ToastIntent.Upload,
            Title = "Uploading file",
            Timeout = 0, // No timeout, will be closed manually
            Content = new ProgressToastContent
            {
                Details = fileName,
                Progress = 0,
            },
        };
        
        ToastService.ShowProgressToast(toast);

        filesBeingUploaded.Add(fileName, toast);
    }
    
    /// <summary>
    /// Updates the progress display in a file upload toast notification.
    /// </summary>
    /// <param name="fileName">The name of the file being uploaded</param>
    /// <param name="progress">The current upload progress percentage (0-100)</param>
    private void UpdateUploadToast(string fileName, int progress)
    {
        if (filesBeingUploaded.ContainsKey(fileName))
        {
            filesBeingUploaded[fileName].Content.Progress = progress;
            ToastService.UpdateToast(fileName, filesBeingUploaded[fileName]);
        }
    }
    
    /// <summary>
    /// Removes a toast notification for a completed or failed file upload.
    /// </summary>
    /// <param name="fileName">The name of the file whose upload has completed or failed</param>
    private void RemoveUploadToast(string fileName)
    {
        if (filesBeingUploaded.ContainsKey(fileName))
        {
            filesBeingUploaded.Remove(fileName);
            ToastService.CloseToast(fileName);
        }
    }

    /// <summary>
    /// Initializes a file upload operation by setting the upload destination.
    /// Called when the upload button is clicked.
    /// </summary>
    private void StartFileUpload()
    {
        uploadLocation = CurrentFolder;
    }

    /// <summary>
    /// Handles progress updates during file upload.
    /// Creates toast notifications, updates progress, and writes file data to disk.
    /// </summary>
    /// <param name="file">Event arguments containing file data and progress information</param>
    async Task OnFileUploadProgressChange(FluentInputFileEventArgs file)
    {
        if (!filesBeingUploaded.ContainsKey(file.Name))
        {
            AddUploadToast(file.Name);
            StateHasChanged();
        }

        UpdateUploadToast(file.Name, file.ProgressPercent);
        
        // Write the file chunk to disk
        var localFile = Path.Combine(uploadLocation, file.Name);
        await file.Buffer.AppendToFileAsync(localFile);
    }

    /// <summary>
    /// Handles the completion of file uploads.
    /// Removes toast notifications and refreshes the folder view.
    /// </summary>
    /// <param name="files">Collection of files that have been uploaded</param>
    void OnFileUploadCompleted(IEnumerable<FluentInputFileEventArgs> files)
    {
        foreach (var file in files)
            RemoveUploadToast(file.Name);
        
        // Find and refresh the folder level that contains the upload location
        int ilevel = FolderLevels.FindIndex(fl => Path.GetFullPath(fl.Path).Equals(Path.GetFullPath(uploadLocation), StringComparison.OrdinalIgnoreCase));
        if (ilevel >= 0)
        {
            FolderLevels[ilevel].ForceRefresh = true;
            FolderLevels[ilevel].RefreshTrigger++;
        }
        
        StateHasChanged();
    }

    /// <summary>
    /// Handles errors during file upload.
    /// Removes the toast notification for the failed upload.
    /// </summary>
    /// <param name="file">Event arguments for the file that failed to upload</param>
    void OnFileUploadError(FluentInputFileEventArgs file)
    {
        RemoveUploadToast(file.Name);
        
        StateHasChanged();
    }
    
    #endregion
    
    #region Delete item

    /// <summary>
    /// Determines whether the delete operation is available.
    /// Delete is available when either a single item is selected, or no items are selected but we're in a subfolder.
    /// The current folder must also be writable.
    /// </summary>
    private bool CanDeleteItem => (SelectedItems.Count == 1 || 
                                 (SelectedItems.Count == 0 && FolderLevels.Count > 1)) &&
                                IsCurrentFolderWritable;
    
    /// <summary>
    /// Shows the delete confirmation dialog.
    /// </summary>
    private async void ShowDeleteConfirmation()
    {
        IsDeleteConfirmationVisible = true;
    }

    /// <summary>
    /// Performs the delete operation when the user confirms.
    /// Handles deletion of selected items or the current folder if no items are selected.
    /// Updates the browser view after deletion.
    /// </summary>
    private async Task ConfirmDeleteAsync()
    {
        bool currentFolderDeleted = false;
        string newCurrentFolder = CurrentFolder;

        if (SelectedItems.Any())
        {
            // Delete selected items
            foreach (var item in SelectedItems)
            {
                try
                {
                    if (item.IsDirectory)
                    {
                        // Check if the folder to be deleted is the current folder or a parent folder
                        if (CurrentFolder.StartsWith(item.Path, StringComparison.OrdinalIgnoreCase))
                        {
                            // Set flag to update CurrentFolder after deletion
                            currentFolderDeleted = true;
                            newCurrentFolder = Path.GetDirectoryName(item.Path);
                        }

                        Directory.Delete(item.Path, true);
                    }
                    else
                    {
                        File.Delete(item.Path);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error deleting item {ItemPath}", item.Path);
                }
            }
            
            // Refresh the view
            FolderLevels.Last().ForceRefresh = true;
            FolderLevels.Last().RefreshTrigger++;

            IsDeleteConfirmationVisible = false;
            SelectedItems.Clear();

            // If the current folder was deleted, navigate to its parent
            if (currentFolderDeleted)
            {
                CurrentFolder = newCurrentFolder;
                await InitializeFolderLevelsAsync();
            }
        }
        else if (FolderLevels.Count > 1)
        {
            // No items selected, delete the current folder
            try
            {
                var parentDirectory = Path.GetDirectoryName(CurrentFolder);

                // Prevent deleting the root directory
                if (string.IsNullOrEmpty(parentDirectory) || CurrentFolder == Path.GetPathRoot(CurrentFolder))
                {
                    Logger.LogWarning("Cannot delete the root directory {CurrentFolder}", CurrentFolder);
                    IsDeleteConfirmationVisible = false;
                    return;
                }

                Directory.Delete(CurrentFolder, true);

                // Update CurrentFolder to parent directory
                CurrentFolder = parentDirectory;
                EditableAddress = GetRelativePath(CurrentFolder);
                
                // Remove last FolderLevel
                FolderLevels.RemoveAt(FolderLevels.Count - 1);
                
                // And force the new last to refresh
                FolderLevels.Last().ForceRefresh = true;
                FolderLevels.Last().RefreshTrigger++;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error deleting current folder {CurrentFolder}", CurrentFolder);
            }

            IsDeleteConfirmationVisible = false;
        }

        StateHasChanged();
    }
    
    #endregion
    
    #region Rename item

    /// <summary>
    /// The file system item to be renamed.
    /// </summary>
    private FileSystemItem ItemToRename { get; set; }

    /// <summary>
    /// Determines whether the rename operation is available.
    /// Rename is available when either a single item is selected, or no items are selected but we're in a subfolder.
    /// The current folder must also be writable.
    /// </summary>
    private bool CanRenameItem => (SelectedItems.Count == 1 || 
                                 (SelectedItems.Count == 0 && FolderLevels.Count > 1)) &&
                                IsCurrentFolderWritable;

    /// <summary>
    /// Initiates the rename operation by showing the rename dialog.
    /// Sets the initial name to the current name of the item.
    /// </summary>
    private void RenameItem()
    {
        if (SelectedItems.Count == 1)
        {
            // Rename the selected item
            ItemToRename = SelectedItems.First();
            NewName = ItemToRename.Name;
            IsRenameModalVisible = true;
        }
        else if (SelectedItems.Count == 0 && FolderLevels.Count > 1)
        {
            // No item selected, rename the current folder
            var currentFolder = CurrentFolder.TrimEnd(Path.DirectorySeparatorChar);
            var directory = Path.GetDirectoryName(currentFolder);
            var currentFolderName = Path.GetFileName(currentFolder);

            NewName = currentFolderName;
            ItemToRename = new FileSystemItem(currentFolder);
            IsRenameModalVisible = true;
        }
    }

    /// <summary>
    /// Performs the rename operation when the user confirms.
    /// Renames the file or directory and updates the browser view.
    /// </summary>
    private async Task ConfirmRenameAsync()
    {
        if (ItemToRename != null && !string.IsNullOrWhiteSpace(NewName))
        {
            var item = ItemToRename;
            var parentDirectory = Path.GetDirectoryName(item.Path);
            var newPath = Path.Combine(parentDirectory, NewName);

            try
            {
                // Perform the rename operation
                if (item.IsDirectory)
                    Directory.Move(item.Path, newPath);
                else
                    File.Move(item.Path, newPath);

                // Refresh the parent FolderLevel to show the updated name
                var parentLevelIndex = FolderLevels.FindIndex(fl => fl.Path.Equals(parentDirectory, StringComparison.OrdinalIgnoreCase));
                if (parentLevelIndex >= 0)
                {
                    // Update the parent level
                    FolderLevels[parentLevelIndex].ForceRefresh = true;
                    FolderLevels[parentLevelIndex].RefreshTrigger++;
                    FolderLevels[parentLevelIndex].SelectedItem = new FileSystemItem(newPath);
                    
                    // Update the current level if it exists
                    if (parentLevelIndex + 1 < FolderLevels.Count)
                    {
                        FolderLevels[parentLevelIndex + 1].Path = newPath;
                        FolderLevels[parentLevelIndex + 1].ForceRefresh = true;
                        FolderLevels[parentLevelIndex + 1].RefreshTrigger++;
                    }
                }

                // Update the selection to point to the renamed item
                if (SelectedItems.Any())
                {
                    SelectedItems.Clear();
                    SelectedItems.Add(new FileSystemItem(newPath));
                }

                // Reset the rename dialog state
                ItemToRename = null;
                IsRenameModalVisible = false;
                NewName = string.Empty;

                // Update address bar
                EditableAddress = GetRelativePath(CurrentFolder);

                StateHasChanged();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error renaming item from {OldPath} to {NewPath}", item.Path, newPath);
            }
        }
    }

    #endregion

    #endregion

    #region SaveFile mode

    /// <summary>
    /// The filename for save file mode.
    /// </summary>
    private string _SaveFileName = "";
    
    /// <summary>
    /// Gets or sets the filename for save file mode.
    /// Validates the filename on change.
    /// </summary>
    private string SaveFileName
    {
        get => _SaveFileName;
        set
        {
            if (_SaveFileName != value)
            {
                _SaveFileName = value;
                ValidateSaveFileName();
                StateHasChanged();
            }
        }
    }
    
    /// <summary>
    /// Error message for save filename validation.
    /// </summary>
    private string SaveFileNameError { get; set; }

    /// <summary>
    /// Validates the save filename against various criteria:
    /// - Non-empty
    /// - Contains valid extension (if AllowedExtensions is specified)
    /// - No invalid path characters
    /// - Destination directory is writable
    /// </summary>
    /// <returns>True if the filename is valid, false otherwise</returns>
    private bool ValidateSaveFileName()
    {
        SaveFileNameError = null;

        // Basic validation for empty filename
        if (string.IsNullOrWhiteSpace(SaveFileName))
        {
            SaveFileNameError = "File name cannot be empty";
            return false;
        }

        // Validate file extension if AllowedExtensions is specified
        if (AllowedExtensions.Length > 0 &&
            !AllowedExtensions.Any(ext => SaveFileName.EndsWith(ext.Replace("*", ""), StringComparison.OrdinalIgnoreCase)))
        {
            SaveFileNameError = $"File must have one of these extensions: {string.Join(", ", AllowedExtensions)}";
            return false;
        }

        // Validate for invalid path characters
        if (SaveFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            SaveFileNameError = "File name contains invalid characters";
            return false;
        }

        // Verify directory is writable for save file mode
        if (!IsCurrentFolderWritable)
        {
            SaveFileNameError = "Cannot save to this location. Directory is not writable.";
            return false;
        }

        return true;
    }

    #endregion

    #region Selection Confirmation

    /// <summary>
    /// Determines whether the Select/Save button should be disabled.
    /// The button is disabled when:
    /// - In SaveFile mode with an invalid filename
    /// - In other modes with no selection (except for SingleFolder mode)
    /// </summary>
    private bool IsSelectButtonDisabled
    {
        get
        {
            if (SelectionMode == SelectionMode.SaveFile)
                return !ValidateSaveFileName();

            return !SelectedItems.Any() && SelectionMode != SelectionMode.SingleFolder;
        }
    }

    /// <summary>
    /// Confirms the current selection and invokes the OnSelectionConfirmed event.
    /// Handles different selection modes appropriately.
    /// </summary>
    private async Task ConfirmSelection()
    {
        if (SelectionMode == SelectionMode.SingleFolder)
        {
            // For folder selection, use the current folder
            SelectedItems.Clear();
            SelectedItems.Add(new FileSystemItem(CurrentFolder));
        }
        else if (SelectionMode == SelectionMode.SaveFile)
        {
            // For save file mode, validate the filename first
            if (!ValidateSaveFileName())
                return;

            // Create a virtual FileSystemItem for the new file
            var newFilePath = Path.Combine(CurrentFolder, SaveFileName);
            var newFileItem = new FileSystemItem(newFilePath)
            {
                Name = SaveFileName,
                IsDirectory = false
            };

            SelectedItems.Clear();
            SelectedItems.Add(newFileItem);
        }

        // Return the selected items to the caller
        await OnSelectionConfirmed.InvokeAsync(SelectedItems);
    }

    /// <summary>
    /// Cancels the selection process and invokes the OnSelectionCanceled event.
    /// </summary>
    private async Task CancelSelection()
    {
        await OnSelectionCanceled.InvokeAsync();
    }

    #endregion

    #region IDisposable Implementation

    /// <summary>
    /// Disposes of the component and releases any JavaScript interop module references.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            // Clean up JavaScript interop resources
            if (_module != null)
                await _module.DisposeAsync();
        }
        catch { }
    }

    #endregion
}
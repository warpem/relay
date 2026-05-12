using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Refund.Components.FileBrowser;

/// <summary>
/// Dialog component that wraps the FileBrowser component for use in a modal dialog.
/// Provides a convenient way to display a file browser in a modal dialog.
/// </summary>
public partial class FileBrowserDialog : ComponentBase, IDialogContentComponent<FileBrowserDialogParameters>
{
    /// <summary>
    /// Reference to the FluentDialog that contains this component.
    /// </summary>
    [CascadingParameter]
    public FluentDialog? Dialog { get; set; }
    
    /// <summary>
    /// Handles the selection confirmation event from the file browser.
    /// Closes the dialog with the selected items as the result.
    /// </summary>
    /// <param name="selectedItems">The list of selected items</param>
    private void SelectionConfirmed(List<FileSystemItem> selectedItems)
    {
        Dialog?.CloseAsync(selectedItems);
    }
    
    /// <summary>
    /// Handles the selection cancellation event from the file browser.
    /// Cancels the dialog without a result.
    /// </summary>
    private void SelectionCanceled()
    {
        Dialog?.CancelAsync();
    }

    /// <summary>
    /// Handles folder navigation changes from the file browser.
    /// Invokes the OnCurrentFolderChanged callback if set.
    /// </summary>
    private async Task HandleCurrentFolderChanged(string folder)
    {
        Content.CurrentFolder = folder;
        if (Content.OnCurrentFolderChanged != null)
            await Content.OnCurrentFolderChanged(folder);
    }

    /// <summary>
    /// Parameters for the dialog content, containing configuration for the file browser.
    /// </summary>
    [Parameter]
    public FileBrowserDialogParameters Content { get; set; }

    /// <summary>
    /// Shows a file browser dialog with the specified parameters.
    /// </summary>
    /// <param name="dialogService">The dialog service to use for showing the dialog</param>
    /// <param name="callbackReceiver">The object that will receive the dialog result callback</param>
    /// <param name="callbackHandler">The function that will handle the dialog result</param>
    /// <param name="title">The title of the dialog</param>
    /// <param name="selectionMode">The selection mode to use (default is SingleFile)</param>
    /// <param name="showFiles">Whether to show files (default is true)</param>
    /// <param name="showFolders">Whether to show folders (default is true)</param>
    /// <param name="allowedExtensions">Array of allowed file extensions, or null for all extensions</param>
    /// <param name="topLevelFolder">The root folder from which browsing will start (default is "/")</param>
    /// <param name="currentFolder">The initial current folder, or null to use the top level folder</param>
    /// <returns>A task that completes when the dialog is shown</returns>
    public static async Task Show(IDialogService dialogService,
                                  object callbackReceiver,
                                  Func<DialogResult, Task> callbackHandler,
                                  string title,
                                  SelectionMode selectionMode = SelectionMode.SingleFile,
                                  bool showFiles = true,
                                  bool showFolders = true,
                                  string[] allowedExtensions = null,
                                  string topLevelFolder = "/",
                                  string currentFolder = null,
                                  bool showSelectionButtons = true,
                                  Func<string, Task> onCurrentFolderChanged = null)
    {
        FileBrowserDialogParameters parameters = new FileBrowserDialogParameters()
        {
            SelectionMode = selectionMode,
            ShowFiles = showFiles,
            ShowFolders = showFolders,
            AllowedExtensions = allowedExtensions ?? [],
            TopLevelFolder = topLevelFolder,
            CurrentFolder = currentFolder,
            ShowSelectionButtons = showSelectionButtons,
            OnCurrentFolderChanged = onCurrentFolderChanged
        };

        await dialogService.ShowDialogAsync<FileBrowserDialog>(parameters,
                                                               new DialogParameters()
                                                               {
                                                                   OnDialogResult = dialogService.CreateDialogCallback(callbackReceiver,
                                                                                                                       callbackHandler),
                                                                   Title = title,
                                                                   Width = "1050px",
                                                                   Height = "696px",
                                                                   TrapFocus = true,
                                                                   Modal = true,
                                                                   PreventScroll = true,
                                                                   PrimaryAction = null,
                                                                   SecondaryAction = null,
                                                                   ShowDismiss = true
                                                               });
    }
}

/// <summary>
/// Parameters for configuring the FileBrowserDialog.
/// </summary>
public class FileBrowserDialogParameters
{
    /// <summary>
    /// The selection mode to use for the file browser.
    /// Determines whether files, folders, or multiple items can be selected.
    /// </summary>
    public SelectionMode SelectionMode { get; set; } = SelectionMode.SingleFile;
    
    /// <summary>
    /// Whether to show files in the browser.
    /// </summary>
    public bool ShowFiles { get; set; } = true;
    
    /// <summary>
    /// Whether to show folders in the browser.
    /// </summary>
    public bool ShowFolders { get; set; } = true;
    
    /// <summary>
    /// Array of allowed file extensions for filtering.
    /// For example: new[] { "*.txt", "*.csv" }
    /// </summary>
    public string[] AllowedExtensions { get; set; } = Array.Empty<string>();
    
    /// <summary>
    /// The root folder from which browsing will start.
    /// Navigation cannot go above this folder.
    /// </summary>
    public string TopLevelFolder { get; set; } = "/";
    
    /// <summary>
    /// The initial current folder.
    /// If null, the top level folder will be used.
    /// </summary>
    public string CurrentFolder { get; set; }

    /// <summary>
    /// Whether to show Select/Cancel buttons at the bottom of the file browser.
    /// Set to false for browse-only mode.
    /// </summary>
    public bool ShowSelectionButtons { get; set; } = true;

    /// <summary>
    /// Optional callback fired when the user navigates to a new folder.
    /// Allows callers to track the current location.
    /// </summary>
    public Func<string, Task> OnCurrentFolderChanged { get; set; }
}
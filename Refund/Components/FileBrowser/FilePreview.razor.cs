using Microsoft.AspNetCore.Components;

namespace Refund.Components.FileBrowser;

/// <summary>
/// Component that provides previews for files in the file browser.
/// Supports previewing images, electron microscopy data files, and text files.
/// </summary>
public partial class FilePreview : ComponentBase
{
    /// <summary>
    /// Cached reference to the previously displayed item.
    /// Used to avoid regenerating previews unnecessarily.
    /// </summary>
    private FileSystemItem _item;

    /// <summary>
    /// The file system item to preview.
    /// </summary>
    [Parameter]
    public FileSystemItem Item { get; set; }

    /// <summary>
    /// Flag indicating whether a preview is currently being generated.
    /// </summary>
    private bool IsPreviewLoading { get; set; } = false;

    /// <summary>
    /// Flag indicating whether the current file is an image that can be previewed.
    /// </summary>
    private bool IsImageFile { get; set; } = false;

    /// <summary>
    /// Flag indicating whether the current file is an electron microscopy data file.
    /// </summary>
    private bool IsEmFile { get; set; } = false;

    /// <summary>
    /// Flag indicating whether the current file is a text file that can be previewed.
    /// </summary>
    private bool IsTextFile { get; set; } = false;

    /// <summary>
    /// The preview content to display.
    /// For images, this is a base64 data URL.
    /// For text files, this is the file content.
    /// </summary>
    private string PreviewContent { get; set; } = null;

    /// <summary>
    /// The active tab ID for the EM file preview tabs.
    /// </summary>
    private string _activePreviewTab = "tab-slice";

    /// <summary>
    /// Called when component parameters are set or updated.
    /// Generates a preview for the item if it has changed.
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        if (Item != _item)
        {
            _item = Item;

            if (Item != null && !Item.IsDirectory)
            {
                // Generate preview for file items
                await GeneratePreviewAsync(Item);
            }
            else
            {
                // Reset preview state for directories or null items
                IsPreviewLoading = false;
                PreviewContent = null;
                IsImageFile = false;
                IsEmFile = false;
                IsTextFile = false;
            }
        }
    }

    /// <summary>
    /// Generates a preview for the specified file system item.
    /// The preview generation method is selected based on the file type:
    /// - Images are converted to base64 data URLs
    /// - EM files are handled by JS-based SlicePreviewJs and IsosurfaceViewer components
    /// - Text files are displayed as plain text
    /// </summary>
    /// <param name="item">The file system item to generate a preview for</param>
    private async Task GeneratePreviewAsync(FileSystemItem item)
    {
        // Reset preview state
        IsPreviewLoading = true;
        PreviewContent = null;
        IsImageFile = false;
        IsEmFile = false;
        IsTextFile = false;
        StateHasChanged();

        // Determine the file type based on extension
        var fileExtension = Path.GetExtension(item.Path).ToLowerInvariant();

        // Generate preview based on file type
        if (IsSupportedImageFormat(fileExtension))
        {
            // Handle standard image formats (JPEG, PNG, etc.)
            IsImageFile = true;

            try
            {
                PreviewContent = item.Path;
            }
            catch
            {
                // Handle exceptions (e.g., file access issues)
                PreviewContent = null;
            }
        }
        else if (IsSupportedEmFormat(fileExtension))
        {
            // EM files are rendered by JS-based components (SlicePreviewJs / IsosurfaceViewer)
            IsEmFile = true;
            _activePreviewTab = "tab-slice";
        }
        else if (IsPlainTextFile(item.Path))
        {
            // Handle text files
            IsTextFile = true;
            // Read a limited number of lines for the preview
            var previewText = await GetTextFilePreviewAsync(item.Path, 2000);
            PreviewContent = previewText;
        }

        // Update preview state
        IsPreviewLoading = false;
        StateHasChanged();
    }

    /// <summary>
    /// Determines whether the file has a supported image format extension.
    /// </summary>
    private bool IsSupportedImageFormat(string extension)
    {
        var supportedFormats = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".svg", ".webp" };
        return supportedFormats.Contains(extension);
    }

    /// <summary>
    /// Determines whether the file has a supported electron microscopy format extension.
    /// </summary>
    private bool IsSupportedEmFormat(string extension)
    {
        var supportedFormats = new[] { ".mrc", ".mrcs", ".map", ".st", ".preali", ".ali", ".rec", ".tif", ".tiff", ".em" };
        return supportedFormats.Contains(extension);
    }

    /// <summary>
    /// Gets the MIME type for a file extension.
    /// </summary>
    private string GetMimeType(string extension)
    {
        return extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            ".webp" => "image/webp",
            _ => "application/octet-stream",
        };
    }

    /// <summary>
    /// Determines whether a file is a plain text file by examining its content.
    /// </summary>
    private bool IsPlainTextFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            char[] buffer = new char[1024];
            int readChars = reader.Read(buffer, 0, buffer.Length);
            string contentSample = new string(buffer, 0, readChars);

            bool isBinary = contentSample.Any(c => char.IsControl(c) && !char.IsWhiteSpace(c) && c != '\r' && c != '\n');
            return !isBinary;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Generates a preview of a text file by reading a limited number of lines.
    /// </summary>
    private async Task<string> GetTextFilePreviewAsync(string path, int maxLines)
    {
        var lines = new List<string>();

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new StreamReader(stream);
            string line;

            while (lines.Count < maxLines && (line = await reader.ReadLineAsync()) != null)
                lines.Add(line);

            line = await reader.ReadLineAsync();
            if (line != null)
                lines.Add("(...)");
        }
        catch
        {
            // Handle exceptions (e.g., file access issues)
        }

        return string.Join("\n", lines);
    }
}

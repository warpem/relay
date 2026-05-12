using Microsoft.AspNetCore.Components;

namespace Refund.Components.FileBrowser;

/// <summary>
/// Component that displays the details of selected file system items in the FileBrowser.
/// Shows properties like name, type, size, and timestamps for files and folders.
/// </summary>
public partial class DetailsColumn : ComponentBase
{
    /// <summary>
    /// The list of file system items to display details for.
    /// </summary>
    [Parameter] 
    public List<FileSystemItem> Items { get; set; }

    /// <summary>
    /// Converts a file size in bytes to a human-readable string with appropriate units.
    /// Formats the size in B, KB, MB, or GB with one decimal place for better readability.
    /// </summary>
    /// <param name="sizeInByte">The size in bytes</param>
    /// <returns>A formatted string representing the file size with appropriate units</returns>
    private string SizeToString(long sizeInByte)
    {
        if (sizeInByte < (1 << 10))
        {
            // Less than 1 KB: display in bytes
            return $"{sizeInByte} B";
        }
        else if (sizeInByte < ((long)1 << 20))
        {
            // Less than 1 MB: display in KB
            return $"{(sizeInByte / 1024.0):N1} KB";
        }
        else if (sizeInByte < ((long)1 << 30))
        {
            // Less than 1 GB: display in MB
            return $"{(sizeInByte / 1024.0 / 1024.0):N1} MB";
        }
        else
        {
            // 1 GB or more: display in GB
            return $"{(sizeInByte / 1024.0 / 1024.0 / 1024.0):N1} GB";
        }
    }
}
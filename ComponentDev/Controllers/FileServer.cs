using System.Net.Mime;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Refund.Services;

namespace ComponentDev.Controllers;

/// <summary>
/// Controller providing secure access to files stored on the server.
/// Implements a secure-by-design file serving mechanism where files are referenced by GUIDs
/// rather than by direct paths, preventing path traversal attacks and unauthorized access.
/// </summary>
[Route("api/file")]
[ApiController]
public class FileServer : ControllerBase
{
    private readonly FileService _guidMappingService;
    private readonly FileExtensionContentTypeProvider _fileExtensionContentTypeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileServer"/> class.
    /// </summary>
    /// <param name="guidMappingService">Service that maps GUIDs to file paths</param>
    public FileServer(FileService guidMappingService)
    {
        _guidMappingService = guidMappingService;
        _fileExtensionContentTypeProvider = new FileExtensionContentTypeProvider();
    }

    /// <summary>
    /// Retrieves a file from the server using its GUID reference.
    /// The GUID is mapped to an actual file path by the FileService, which ensures
    /// that only files explicitly registered with the service can be accessed.
    /// </summary>
    /// <param name="guid">The GUID that maps to a file path</param>
    /// <returns>File content with appropriate content type, or NotFound if the GUID is invalid or the file doesn't exist</returns>
    [HttpGet("{guid}")]
    public IActionResult GetFile(string guid)
    {
        if(!_guidMappingService.TryGetPath(guid, out var filePath))
        {
            return NotFound();
        }

        if(!System.IO.File.Exists(filePath))
        {
            return NotFound();
        }

        // Resolve symlinks so PhysicalFile gets the real file length for range processing
        var resolvedTarget = new FileInfo(filePath).ResolveLinkTarget(returnFinalTarget: true);
        if (resolvedTarget != null)
            filePath = resolvedTarget.FullName;

        if(!_fileExtensionContentTypeProvider.TryGetContentType(filePath, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        var contentDisposition = new ContentDisposition
        {
            FileName = Path.GetFileName(filePath),
            Inline = contentType.StartsWith("image/") // Display images inline in browser
        };

        Response.Headers.Append("Content-Disposition", contentDisposition.ToString());

        // Use PhysicalFile with range processing for efficient partial content requests
        return PhysicalFile(filePath, contentType, enableRangeProcessing: true);
    }

    /// <summary>
    /// Checks if a directory path is accessible and has read/write permissions.
    /// Used to verify that a directory can be used for data storage before attempting operations.
    /// </summary>
    /// <param name="path">The directory path to check</param>
    /// <returns>
    /// OK with readable/writable flags if the directory exists and has appropriate permissions,
    /// BadRequest if the path is invalid, NotFound if the directory doesn't exist,
    /// Forbidden if the directory is not accessible, or Internal Server Error for other issues
    /// </returns>
    [HttpPost("check-permissions")]
    public IActionResult CheckPermissions([FromQuery] string path)
    {
        if(string.IsNullOrEmpty(path))
        {
            return BadRequest(new { Message = "Directory path is required." });
        }

        if(!Directory.Exists(path))
        {
            return NotFound(new { Message = "Directory does not exist." });
        }

        try
        {
            // Test write permissions by creating a temporary file
            var testFilePath = Path.Combine(path, "test_permission.tmp");

            using(var fs = System.IO.File.Create(testFilePath, 1, FileOptions.DeleteOnClose)) { }

            return Ok(new
            {
                Readable = true,
                Writable = true,
                Message = "Directory is readable and writable."
            });
        }
        catch(UnauthorizedAccessException ex)
        {
            return StatusCode(403, new
            {
                Readable = false,
                Writable = false,
                Error = ex.Message
            });
        }
        catch(Exception ex)
        {
            return StatusCode(500, new
            {
                Error = ex.Message
            });
        }
    }
}
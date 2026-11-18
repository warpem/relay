using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Refund.Services;
using Relay.Controllers;
using Xunit;

namespace Relay.Tests;

public class FileServerTests
{
    [Fact]
    public void DownloadFilenameIsUsedInContentDispositionEvenForSymlinks()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string target = Path.Combine(root, "original.png");
            File.WriteAllText(target, "test");
            string link = Path.Combine(root, "result.png");
            File.CreateSymbolicLink(link, target);
            var files = new FileService(NullLogger<FileService>.Instance);
            string token = files.GetUrl(link).Split('/').Last();
            var controller = new FileServer(files)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            Assert.IsType<PhysicalFileResult>(controller.GetFile(token, "P12_S34_result.png"));
            string disposition = controller.Response.Headers.ContentDisposition.ToString();
            Assert.Contains("P12_S34_result.png", disposition);
            Assert.StartsWith("attachment;", disposition);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

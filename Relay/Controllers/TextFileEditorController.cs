using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Refund.Services;

namespace Relay.Controllers;

[ApiController]
[Route("api/text-editor")]
[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class TextFileEditorController(TextFileEditorService files, IAntiforgery antiforgery) : ControllerBase
{
    public record DestinationRequest(string Path);

    [HttpGet("{token}")]
    public Task<IActionResult> Read(string token, CancellationToken cancellation) => Execute(async () =>
    {
        var ticket = files.OpenTicket(token, User.Identity?.Name);
        var document = await files.DescribeAsync(ticket, cancellation);
        return Ok(new
        {
            document.Path, document.Size, document.Encoding, document.LineEnding,
            ticket.Root,
            token = files.CreateTicket(document.Path, ticket.Root, ticket.User, document.Encoding),
            antiforgeryToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken
        });
    });

    [HttpGet("{token}/content")]
    public Task<IActionResult> Content(string token, CancellationToken cancellation) => Execute(async () =>
    {
        var ticket = files.OpenTicket(token, User.Identity?.Name);
        Response.ContentType = "text/plain; charset=utf-8";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        await files.CopyContentAsync(ticket, Response.Body, cancellation);
        return new EmptyResult();
    });

    // Confirm existing Save As destinations before sending a potentially very large upload.
    [HttpPost("{token}/destination")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Destination(string token, DestinationRequest request) => Execute(() =>
    {
        var ticket = files.OpenTicket(token, User.Identity?.Name);
        var destination = files.PrepareDestination(ticket, request.Path);
        return Task.FromResult<IActionResult>(Ok(new
        {
            destination.Path, destination.Exists,
            token = files.CreateTicket(destination.Path, ticket.Root, ticket.User, ticket.Encoding)
        }));
    });

    // The request body streams directly to a staging file, outside MVC's JSON model binding.
    [HttpPut("{token}")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(TextFileEditorService.MaximumBytes)]
    [Consumes("text/plain", "application/octet-stream")]
    public Task<IActionResult> Save(string token, CancellationToken cancellation) => Execute(async () =>
    {
        var ticket = files.OpenTicket(token, User.Identity?.Name);
        await files.SaveAsync(ticket, Request.Body, cancellation);
        return Ok(new
        {
            ticket.Path,
            token = files.CreateTicket(ticket.Path, ticket.Root, ticket.User, ticket.Encoding)
        });
    });

    private async Task<IActionResult> Execute(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (Exception) when (Response.HasStarted)
        {
            // A failed streamed download must terminate, rather than appear successfully truncated.
            HttpContext.Abort();
            return new EmptyResult();
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
        catch (FileNotFoundException) { return NotFound(new { error = "The file no longer exists." }); }
        catch (DirectoryNotFoundException) { return NotFound(new { error = "The destination folder does not exist." }); }
        catch (BadHttpRequestException ex) { return StatusCode(ex.StatusCode, new { error = ex.Message }); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or NotSupportedException)
        { return BadRequest(new { error = ex.Message }); }
        catch (IOException) { return StatusCode(409, new { error = "Unable to access the file. It may be locked or unavailable." }); }
    }
}

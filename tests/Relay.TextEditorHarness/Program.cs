using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Refund.Services;

// Local-only integration harness: real production controller, file service, Monaco and cookies.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = Directory.GetCurrentDirectory() });
builder.Services.AddControllersWithViews();
builder.Services.AddAntiforgery();
builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
builder.Services.AddSingleton<TextFileEditorService>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
builder.Services.AddAuthorization();
var app = builder.Build();
string root = Path.Combine(Path.GetTempPath(), "relay-editor-harness-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string input = Path.Combine(root, "input.json");
await File.WriteAllTextAsync(input, "{\r\n  \"hello\": \"world\"\r\n}\r\n", new System.Text.UTF8Encoding(true));
// Representative wide STAR-style rows, generated in bounded chunks.
int largeMiB = int.TryParse(Environment.GetEnvironmentVariable("EDITOR_TEST_MIB"), out int configured) ? configured : 100;
await using (var large = File.Create(Path.Combine(root, "large.star")))
{
    byte[] chunk = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(new string('1', 511) + "\n", 2048)));
    for (int i = 0; i < largeMiB; i++) await large.WriteAsync(chunk);
}
app.Lifetime.ApplicationStopped.Register(() => Directory.Delete(root, true));
app.UseStaticFiles(new StaticFileOptions
{
    RequestPath = "/_content/Refund",
    FileProvider = new PhysicalFileProvider(Path.GetFullPath("../../Refund/wwwroot", builder.Environment.ContentRootPath))
});
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/sign-in", async (HttpContext context) =>
{
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Redirect("/");
});
app.MapGet("/", (TextFileEditorService files, HttpContext context) =>
{
    string path = context.Request.Query.ContainsKey("large") ? Path.Combine(root, "large.star") : input;
    string token = files.CreateTicket(path, root, "alice");
    return Results.Content($$"""
        <!DOCTYPE html><html><head><title>Relay editor test</title></head><body>
        <button id="edit">Edit</button>
        <script type="module">
        import { open } from '/_content/Refund/js/textFileEditor.js';
        window.editorToken = '{{token}}';
        document.querySelector('#edit').onclick = () => open('test', window.editorToken, true,
            { invokeMethodAsync: async (...args) => { window.closedEditor = args; } });
        </script></body></html>
        """, "text/html");
});
app.Run();

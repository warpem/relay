using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Refund.Components;
using Refund.Services;

namespace Refund.Tests.Components;

public sealed class ServerFigureTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "relay figure " + Guid.NewGuid());

    [Fact]
    public async Task PrefersAdaptiveSvgAndOffersFixedDownloads()
    {
        Directory.CreateDirectory(_directory);
        string png = Path.Combine(_directory, "fsc.png");
        foreach (string extension in new[] { ".png", ".svg", ".light.svg", ".dark.svg" })
            File.WriteAllText(Path.ChangeExtension(png, extension), "figure");
        await Render(png, (html, files) =>
        {
            Assert.Contains($"src=\"{files.GetUrl(Path.ChangeExtension(png, ".svg"))}?v=iteration-7\"", html);
            Assert.Contains("download=\"fsc.light.svg\"", html);
            Assert.Contains("download=\"fsc.dark.svg\"", html);
            Assert.Contains("download=\"fsc.png\"", html);
            Assert.Contains("alt=\"Fourier shell correlation\"", html);
            Assert.Contains("theme-figure", html);
        });
    }

    [Fact]
    public async Task ExistingPngOnlyJobsRemainViewable()
    {
        Directory.CreateDirectory(_directory);
        string png = Path.Combine(_directory, "fsc.png");
        File.WriteAllText(png, "figure");
        await Render(png, (html, files) =>
        {
            Assert.Contains($"src=\"{files.GetUrl(png)}?v=iteration-7\"", html);
            Assert.DoesNotContain(".svg", html);
        });
    }

    [Fact]
    public async Task MissingIterationHasNoBrokenImageOrDownloadLinks()
    {
        await Render(Path.Combine(_directory, "missing.png"), (html, _) =>
        {
            Assert.Contains("Figure not available", html);
            Assert.DoesNotContain("<img", html);
            Assert.DoesNotContain("<a ", html);
        });
    }

    private static async Task Render(string path, Action<string, FileService> check)
    {
        await using var services = new ServiceCollection().AddLogging().AddSingleton<FileService>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<ServerFigure>(ParameterView.FromDictionary(
                new Dictionary<string, object?> {
                    [nameof(ServerFigure.ServerPath)] = path,
                    [nameof(ServerFigure.Title)] = "Fourier shell correlation",
                    [nameof(ServerFigure.CacheKey)] = "iteration-7"
                }));
            check(component.ToHtmlString(), services.GetRequiredService<FileService>());
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}

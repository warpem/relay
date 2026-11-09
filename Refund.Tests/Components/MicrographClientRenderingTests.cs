using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Refund.Components.MicrographViewer;
using Refund.Services;

namespace Refund.Tests.Components;

public class MicrographClientRenderingTests
{
    [Fact]
    public async Task RenderingKeepsPixelFilesAndBuffersOutOfBlazor()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddFluentUIComponents();
        services.AddSingleton<FileService>();
        services.AddSingleton<IJSRuntime, NoJs>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<MicrographViewer>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(MicrographViewer.MicrographPath)] = "2@/missing.mrcs",
                    [nameof(MicrographViewer.MaskPath)] = "/missing-mask.mrc",
                    [nameof(MicrographViewer.ThumbnailPath)] = "/missing.png",
                    [nameof(MicrographViewer.ParticleStarPath)] = "/missing.star",
                    [nameof(MicrographViewer.MinWidth)] = 320,
                    [nameof(MicrographViewer.MinHeight)] = 240
                }));
            var html = rendered.ToHtmlString();
            Assert.Contains("data-micrograph-surface", html);
            Assert.Contains("micrograph-controls", html);
            Assert.Contains("Particles", html);
            Assert.Contains("Mask", html);
            Assert.DoesNotContain("base64", html);
            Assert.DoesNotContain("<image", html);
            Assert.DoesNotContain("<canvas", html);
            Assert.DoesNotContain("/missing", html);
        });
    }
    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException("Static rendering must not load pixels");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }
}

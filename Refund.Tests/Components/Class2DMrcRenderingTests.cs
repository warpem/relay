using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Refund.Components.Class2DSelection;
using Refund.Services;

namespace Refund.Tests.Components;

public class Class2DMrcRenderingTests
{
    [Fact]
    public async Task BlazorRendersOnlyAnEmptyHostWithoutReadingPixelFiles()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddFluentUIComponents();
        services.AddSingleton<FileService>();
        services.AddSingleton<IJSRuntime, NoJs>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<Class2DSelection>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(Class2DSelection.MrcStackPath)] = "/this-file-does-not-exist.mrcs",
                    [nameof(Class2DSelection.ImageNumbers)] = new[] { 150, 1 },
                    [nameof(Class2DSelection.ClassIds)] = new[] { 9, 2 },
                    [nameof(Class2DSelection.AllowSorting)] = false,
                    [nameof(Class2DSelection.RenderedImageSidelengthPx)] = 128
                }));
            string html = rendered.ToHtmlString();
            Assert.Contains("id=\"class2d-", html);
            Assert.DoesNotContain("<img", html);
            Assert.DoesNotContain("#9", html);
            Assert.DoesNotContain("<select", html);
            Assert.DoesNotContain("base64", html);
            Assert.DoesNotContain("background-image", html);
            Assert.DoesNotContain("<canvas", html);
        });
    }

    [Fact]
    public void SelectionBridgeFiltersImageNumbersAndRejectsStaleOrReadOnlyCallbacks()
    {
        int notifications = 0;
        var component = new Class2DSelection
        {
            RenderedImageSidelengthPx = 128, SpacingPx = 6,
            ImageNumbers = [150, 1],
            Subclasses = [new(1, "/missing.mrcs", [new(1, 101, "#1.1", null, .5f), new(2, 102, "#1.2", null, .5f)])],
            OnSelectionChanged = () => notifications++
        };
        component.ReceiveSelection([150, 1, 999], 0);
        Assert.Equal(new[] { 1, 150 }, component.SelectedClasses.Order());
        Assert.Equal(1, notifications);
        component.ReceiveSubclassSelection([102, 999], 0);
        Assert.Equal(new[] { 102 }, component.SelectedSubclasses);
        Assert.Equal(2, notifications);
        component.ReceiveSubclassSelection([], -1);
        Assert.Single(component.SelectedSubclasses);
        component.ReceiveSelection([], -1);
        Assert.Equal(2, component.SelectedClasses.Count);
        component.SelectionIsMutable = false;
        component.ReceiveSelection([], 0);
        Assert.Equal(2, component.SelectedClasses.Count);
        component.ReceiveSubclassSelection([], 0);
        Assert.Single(component.SelectedSubclasses);
        Assert.Equal(2, notifications);
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException("Static rendering must not run the image loader");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }
}

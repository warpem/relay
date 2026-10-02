using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Refund.Components;

namespace Refund.Tests.Components;

public sealed class ComboButtonRenderingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MenuContentsAreOnlyMountedWhileOpen(bool open)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFluentUIComponents();
        services.AddSingleton<IJSRuntime, NoJsRuntime>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        int menuRenders = 0;
        RenderFragment menu = builder =>
        {
            menuRenders++;
            builder.AddContent(0, "Expensive job list");
        };

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<ComboButton>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(ComboButton.IsOpen)] = open,
                    [nameof(ComboButton.MenuContent)] = menu
                }));

            if (open)
            {
                Assert.True(menuRenders > 0);
                Assert.Contains("Expensive job list", rendered.ToHtmlString());
            }
            else
            {
                Assert.Equal(0, menuRenders);
                Assert.DoesNotContain("Expensive job list", rendered.ToHtmlString());
            }
        });
    }

    private sealed class NoJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException("Static rendering must not call JavaScript.");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }
}

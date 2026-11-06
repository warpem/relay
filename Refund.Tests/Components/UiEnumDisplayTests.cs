using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Refund.Jobs.Common.Import.ImportParticlePositions;
using Refund.Jobs.Refinement.Classes3D.BeamClass3D;
using Refund.Jobs.Refinement.Refinement3D.BeamRefine3D;
using Refund.UIFields;

namespace Refund.Tests.Components;

public class UiEnumDisplayTests
{
    [Fact]
    public void ExistingAnnotationsAreHonoredAndUnannotatedNamesArePreserved()
    {
        var files = new UiEnum("", "Input", typeof(InputTypes));
        Assert.Equal("Single file", files.GetOptionLabel(nameof(InputTypes.SingleFile)));
        Assert.Equal("Per-series files", files.GetOptionLabel(nameof(InputTypes.MultipleFiles)));
        Assert.Equal("GPU", new UiEnum("", "Device", typeof(BeamDevice)).GetOptionLabel(nameof(BeamDevice.GPU)));
    }

    [Fact]
    public async Task DropdownDisplaysAliasesButKeepsOriginalEnumOptionValues()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddFluentUIComponents();
        services.AddSingleton<IJSRuntime, NoJs>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<UiEnumView>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(UiEnumView.FieldDescription)] = new UiEnum("", "Schedule", typeof(BeamClassificationParticleSchedule)),
                    [nameof(UiEnumView.Value)] = BeamClassificationParticleSchedule.Quadratic
                }));
            var html = rendered.ToHtmlString();
            Assert.Contains("Quadratic ramp-up", html);
            Assert.Contains("Step to full set", html);
            Assert.Contains("value=\"Quadratic\"", html);
            Assert.Contains("value=\"Step\"", html);
            Assert.DoesNotContain("value=\"Quadratic ramp-up\"", html);
        });
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException("Static rendering must not invoke JavaScript");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }
}

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Refund.Components;
using System.ComponentModel.DataAnnotations;

namespace Refund.Tests.Components;

public sealed class RelayTextInputEditContextTests
{
    public class Model
    {
        [Required, MinLength(3)] public string? Username { get; set; }
        [Required] public string? Name { get; set; }
    }

    public sealed class Form : ComponentBase
    {
        public static Form? Current; public Model M = new();
        protected override void OnInitialized() => Current = this;
        public EditContext? Ctx;
        protected override void BuildRenderTree(RenderTreeBuilder b)
        {
            b.OpenComponent<EditForm>(0);
            b.AddComponentParameter(1, "Model", M);
            b.AddComponentParameter(2, "ChildContent", (RenderFragment<EditContext>)(ctx => bb =>
            {
                Ctx = ctx;
                bb.OpenComponent<DataAnnotationsValidator>(0); bb.CloseComponent();
                bb.OpenComponent<RelayTextInput>(1);
                bb.AddComponentParameter(2, "Value", M.Username);
                bb.AddComponentParameter(3, "ValueChanged", EventCallback.Factory.Create<string>(this, (Action<string>)(v => { M.Username = v; ctx.NotifyFieldChanged(ctx.Field(nameof(Model.Username))); })));
                bb.AddComponentParameter(4, "ValueExpression", (System.Linq.Expressions.Expression<Func<string>>)(() => M.Username!));
                bb.AddComponentParameter(5, "Immediate", true);
                bb.AddComponentParameter(6, "Required", true);
                bb.CloseComponent();
            }));
            b.CloseComponent();
        }
        public Task Type(string v) => InvokeAsync(() => { M.Username = v; Ctx!.NotifyFieldChanged(Ctx.Field(nameof(Model.Username))); StateHasChanged(); });
    }

    [Fact]
    public async Task TypingIntoAnInitiallyNullFieldKeepsTheEditContext()
    {
        var services = new ServiceCollection();
        services.AddLogging(); services.AddFluentUIComponents();
        services.AddSingleton<IJSRuntime, NoJs>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var c = await renderer.RenderComponentAsync<Form>(ParameterView.Empty);
            var f = Form.Current!;
            await f.Type("a"); await f.Type("ab");
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(c.ToHtmlString(), "minimum length").Count); await f.Type("abcd"); await f.Type("");
            Assert.NotNull(f.Ctx);
        });
    }
    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => default;
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken ct, object?[]? args) => default;
    }
}

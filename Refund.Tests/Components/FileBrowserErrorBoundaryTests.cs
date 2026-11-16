using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Refund.Components.FileBrowser;

namespace Refund.Tests.Components;

/// <summary>
/// The file browser eats every file system oddity; when one slips through, the dialog must contain
/// it and offer recovery rather than letting it tear down the Blazor circuit.
/// </summary>
public sealed class FileBrowserErrorBoundaryTests
{
    [Fact]
    public async Task BrowserFailureShowsRecoverableErrorAndRetryRecreatesTheBrowser()
    {
        var activator = new SwappingActivator { BrowserFails = true };
        var content = new FileBrowserDialogParameters { TopLevelFolder = "/data", CurrentFolder = "/data/run/tasks" };

        await using var harness = await Harness.RenderAsync(activator, content);
        string html = harness.Html();
        Assert.Contains("The file browser ran into a problem", html);
        Assert.Contains("Could not find file &#x27;/data/run/tasks/hb-113&#x27;", html);
        Assert.Contains("/data/run/tasks", html);
        Assert.Contains("Go to top folder", html);
        Assert.IsType<FileNotFoundException>(Assert.Single(harness.LoggedErrors));

        activator.BrowserFails = false;
        await harness.InvokeDialogAsync("Retry");
        html = harness.Html();
        Assert.Contains("browser-ok:/data/run/tasks", html);
        Assert.DoesNotContain("ran into a problem", html);
    }

    [Fact]
    public async Task GoToTopFolderRecreatesTheBrowserAtTheTopLevelFolder()
    {
        var activator = new SwappingActivator { BrowserFails = true };
        var content = new FileBrowserDialogParameters { TopLevelFolder = "/data", CurrentFolder = "/data/unreadable" };

        await using var harness = await Harness.RenderAsync(activator, content);
        activator.BrowserFails = false;
        await harness.InvokeDialogAsync("GoToTopLevelFolder");

        Assert.Equal("/data", content.CurrentFolder);
        Assert.Contains("browser-ok:/data", harness.Html());
    }

    [Fact]
    public async Task TopFolderButtonIsHiddenWhenAlreadyAtTheTop()
    {
        var activator = new SwappingActivator { BrowserFails = true };
        var content = new FileBrowserDialogParameters { TopLevelFolder = "/data/", CurrentFolder = "/data" };

        await using var harness = await Harness.RenderAsync(activator, content);
        Assert.DoesNotContain("Go to top folder", harness.Html());
    }

    private sealed class Harness : IAsyncDisposable
    {
        private ServiceProvider _provider = default!;
        private HtmlRenderer _renderer = default!;
        private HtmlRootComponent _root;
        private SwappingActivator _activator = default!;

        public static async Task<Harness> RenderAsync(SwappingActivator activator, FileBrowserDialogParameters content)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddFluentUIComponents();
            services.AddSingleton<IJSRuntime, NoJsRuntime>();
            services.AddSingleton<IErrorBoundaryLogger>(sp => sp.GetRequiredService<RecordingErrorLogger>());
            services.AddSingleton<RecordingErrorLogger>();
            services.AddSingleton<IComponentActivator>(provider => activator.WithServices(provider));
            var harness = new Harness { _activator = activator, _provider = services.BuildServiceProvider() };
            harness._renderer = new HtmlRenderer(harness._provider, harness._provider.GetRequiredService<ILoggerFactory>());
            harness._root = await harness._renderer.Dispatcher.InvokeAsync(() =>
                harness._renderer.RenderComponentAsync<FileBrowserDialog>(ParameterView.FromDictionary(
                    new Dictionary<string, object?> { [nameof(FileBrowserDialog.Content)] = content })));
            await harness._root.QuiescenceTask;
            return harness;
        }

        public IReadOnlyList<Exception> LoggedErrors => _provider.GetRequiredService<RecordingErrorLogger>().Errors;

        public string Html() => _renderer.Dispatcher.InvokeAsync(() => _root.ToHtmlString()).GetAwaiter().GetResult();

        public async Task InvokeDialogAsync(string method)
        {
            var dialog = _activator.Dialog!;
            await _renderer.Dispatcher.InvokeAsync(() =>
                typeof(FileBrowserDialog).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, null));
            await _root.QuiescenceTask;
        }

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _provider.DisposeAsync();
        }
    }

    /// <summary>
    /// Stands in for <see cref="FileBrowser"/> so the test controls whether it fails, without a real file system.
    /// </summary>
    private sealed class SwappingActivator : IComponentActivator
    {
        private IServiceProvider _services = default!;
        public bool BrowserFails { get; set; }
        public FileBrowserDialog? Dialog { get; private set; }

        public SwappingActivator WithServices(IServiceProvider services)
        {
            _services = services;
            return this;
        }

        public IComponent CreateInstance(Type componentType)
        {
            if (componentType == typeof(FileBrowser))
                return new FakeBrowser(BrowserFails);
            var component = (IComponent)ActivatorUtilities.CreateInstance(_services, componentType);
            if (component is FileBrowserDialog dialog)
                Dialog = dialog;
            return component;
        }
    }

    private sealed class FakeBrowser(bool fails) : ComponentBase
    {
        private string _folder = "";

        public override Task SetParametersAsync(ParameterView parameters)
        {
            if (fails)
                throw new FileNotFoundException("Could not find file '/data/run/tasks/hb-113'.");
            _folder = parameters.GetValueOrDefault<string>(nameof(FileBrowser.CurrentFolder)) ?? "";
            return base.SetParametersAsync(ParameterView.Empty);
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, "browser-ok:" + _folder);
    }

    private sealed class RecordingErrorLogger : IErrorBoundaryLogger
    {
        public List<Exception> Errors { get; } = [];
        public ValueTask LogErrorAsync(Exception exception)
        {
            Errors.Add(exception);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException("Static rendering must not call JavaScript.");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }
}

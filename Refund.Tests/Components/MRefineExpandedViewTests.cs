using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Refund.DataModel;
using Refund.JobResources;
using Refund.Jobs.M.Refine;
using Refund.Services;
using Refund.Services.Core.Session;

namespace Refund.Tests.Components;

[Collection("JobRegistry")]
public sealed class MRefineExpandedViewTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OpeningJobRendersFirstSpeciesWithoutClicking(int speciesCount)
    {
        await using var view = new ViewHarness();
        var job = view.CreateJob(1, speciesCount);

        await view.OpenAsync(job);

        Assert.Equal("species-Alpha%20species", view.Tabs.ActiveTabId);
        string html = await view.HtmlAsync();
        Assert.Contains("FSC curves for Alpha species", html);
        Assert.DoesNotContain("FSC curves for Zulu", html);
    }

    [Fact]
    public async Task TabSelectionSurvivesUpdatesAndResetsForAnotherJob()
    {
        await using var view = new ViewHarness();
        var job = view.CreateJob(1, 2);
        await view.OpenAsync(job);

        await view.SelectAsync("species-Zulu");
        await view.UpdateAsync();
        Assert.Equal("species-Zulu", view.Tabs.ActiveTabId);
        string html = await view.HtmlAsync();
        Assert.Contains("FSC curves for Zulu", html);
        Assert.DoesNotContain("FSC curves for Alpha species", html);

        await view.OpenAsync(view.CreateJob(2, 2));
        Assert.Equal("species-Alpha%20species", view.Tabs.ActiveTabId);
        Assert.Contains("FSC curves for Alpha species", await view.HtmlAsync());
    }

    [Fact]
    public async Task FirstSpeciesLoadsWhenResultsBecomeAvailableWhileOpen()
    {
        await using var view = new ViewHarness();
        var job = view.CreateJob(1, 2);
        job.VisAvailableIteration = -1;
        await view.OpenAsync(job);
        Assert.DoesNotContain("FSC curves for", await view.HtmlAsync());

        job.VisAvailableIteration = 0;
        await view.UpdateAsync();

        Assert.Equal("species-Alpha%20species", view.Tabs.ActiveTabId);
        Assert.Contains("FSC curves for Alpha species", await view.HtmlAsync());
    }

    private sealed class ViewHarness : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "relay-m-tabs-" + Guid.NewGuid().ToString("N"));
        private readonly ServiceProvider _services;
        private readonly HtmlRenderer _renderer;
        private readonly RecordingActivator _activator = new();
        private Func<string>? _html;

        public FluentTabs Tabs => _activator.Tabs!;

        public ViewHarness()
        {
            JobRegistry.EnsurePopulated();
            var session = new RelaySession(null!, null!, null!, null!);
            var expandedView = new ExpandedJobViewService(null!, session, NullLogger<ExpandedJobViewService>.Instance);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddFluentUIComponents();
            services.AddSingleton(expandedView);
            services.AddSingleton(new FileService(NullLogger<FileService>.Instance));
            services.AddSingleton<IJSRuntime, NoJsRuntime>();
            services.AddSingleton<IComponentActivator>(_activator);
            _services = services.BuildServiceProvider();
            _renderer = new HtmlRenderer(_services, _services.GetRequiredService<ILoggerFactory>());
        }

        public Refine CreateJob(int id, int speciesCount)
        {
            var job = new Refine { Id = id, Space = new Space { RootDirectory = _directory }, VisAvailableIteration = 0 };
            // Deliberately unsorted, with a space in the first displayed species' name.
            var names = speciesCount == 1 ? new[] { "Alpha species" } : new[] { "Zulu", "Alpha species" };
            var population = new MPopulation([], names.Select(name => new MSpecies { Name = name }).ToList());
            var input = job.PortsIn[Refine.PortInPopulation];
            input.Edges.Add(new Edge
            {
                Source = new PortOut(job, typeof(MPopulation), "source", "source", _ => population),
                Target = input
            });
            Directory.CreateDirectory(job.RelayResultsDirectoryPath);
            foreach (string name in names)
                File.WriteAllText(job.VisFsc(name), "rendering test image placeholder");
            return job;
        }

        public Task OpenAsync(Refine job) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            if (_html == null)
            {
                var root = await _renderer.RenderComponentAsync<RefineExpandedView>();
                _html = root.ToHtmlString;
            }
            await InvokeViewAsync("HandleJobChanged", job.AsReadOnly());
        });

        public Task UpdateAsync() => _renderer.Dispatcher.InvokeAsync(() => InvokeViewAsync("HandleJobUpdated"));
        public Task SelectAsync(string id) => _renderer.Dispatcher.InvokeAsync(() => Tabs.GoToTabAsync(id));
        public Task<string> HtmlAsync() => _renderer.Dispatcher.InvokeAsync(() => _html!());

        private Task InvokeViewAsync(string method, params object[] args) =>
            (Task)typeof(RefineExpandedView).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(_activator.View, args)!;

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _services.DisposeAsync();
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, true);
        }
    }

    private sealed class RecordingActivator : IComponentActivator
    {
        public RefineExpandedView? View { get; private set; }
        public FluentTabs? Tabs { get; private set; }

        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is RefineExpandedView view) View = view;
            if (component is FluentTabs tabs) Tabs = tabs;
            return component;
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

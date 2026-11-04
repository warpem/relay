using System.Reflection;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Refund.Configuration;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Jobs.Common.Notes.Note;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Repositories;
using Refund.Services.Core.Session;
using Relay.Panels.Right;
using Xunit;

namespace Relay.Tests;

public sealed class JobPropertiesTests
{
    [Theory]
    [InlineData("job-details")]
    [InlineData("job-parameters")]
    public async Task SwitchingJobsRefreshesContentAndKeepsTheSelectedTab(string tabId)
    {
        await using var harness = await Harness.CreateAsync();
        await harness.ShowAsync(harness.First);
        await harness.Renderer.Dispatcher.InvokeAsync(() => harness.Activator.Tabs!.SelectTabAsync(tabId));

        await harness.ShowAsync(harness.Second);
        string html = await harness.HtmlAsync();
        Assert.Contains("Second job", html);
        Assert.Contains("second-job-note", html);
        Assert.DoesNotContain("first-job-note", html);
        Assert.Contains("second-job-parameter", html);
        Assert.DoesNotContain("first-job-parameter", html);
        Assert.Equal(tabId, harness.Activator.Tabs!.ActiveTabId);

        await harness.ShowAsync(harness.First);
        html = await harness.HtmlAsync();
        Assert.Contains("first-job-note", html);
        Assert.DoesNotContain("second-job-note", html);
        Assert.Contains("first-job-parameter", html);
        Assert.DoesNotContain("second-job-parameter", html);
        Assert.Equal(tabId, harness.Activator.Tabs!.ActiveTabId);
    }

    [Fact]
    public async Task SwitchingToABuildingJobFallsBackFromParametersToDetails()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.ShowAsync(harness.First);
        await harness.Renderer.Dispatcher.InvokeAsync(() => harness.Activator.Tabs!.SelectTabAsync("job-parameters"));
        await harness.Manager.UpdateJob(harness.User, harness.Second, j => j.Status = JobStatus.Building);
        await harness.ShowAsync(harness.Second);
        Assert.Equal("job-details", harness.Activator.Tabs!.ActiveTabId);
        Assert.Contains("second-job-note", await harness.HtmlAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackgroundUpdatesWaitForRenderingAndSkipDisposedPanels(bool dispose)
    {
        await using var harness = await Harness.CreateAsync();
        await harness.ShowAsync(harness.First);
        var panel = harness.Activator.Properties!;
        var iterations = (List<int>)typeof(JobProperties)
            .GetField("_iterationsWithResults", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        var update = typeof(JobProperties)
            .GetMethod("HandleJobUpdated", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task? pendingUpdate = null;

        await harness.Renderer.Dispatcher.InvokeAsync(() =>
        {
            iterations.Add(17);
            using var enumerator = iterations.GetEnumerator();
            Assert.True(enumerator.MoveNext());
            using var queued = new ManualResetEventSlim();
            // Hold the dispatcher as a render would while a notification arrives.
            var background = Task.Run(() =>
            {
                try
                {
                    pendingUpdate = (Task)update.Invoke(panel, new object[] { new GroupEventArgs<ReadOnlyJob>(harness.First) })!;
                }
                finally
                {
                    queued.Set();
                }
            });
            Assert.True(queued.Wait(TimeSpan.FromSeconds(5)));
            background.GetAwaiter().GetResult();
            Assert.False(pendingUpdate!.IsCompleted);
            Assert.False(enumerator.MoveNext());
            if (dispose) panel.Dispose();
        });

        await pendingUpdate!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(dispose, iterations.Contains(17));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "relay-properties-" + Guid.NewGuid().ToString("N"));
        private ServiceProvider _services = null!;
        private HtmlRootComponent? _root;
        public DataManager Manager { get; }
        public HtmlRenderer Renderer { get; private set; } = null!;
        public RecordingActivator Activator { get; } = new();
        public ReadOnlyUser User { get; private set; } = null!;
        public ReadOnlyJob First { get; private set; } = null!;
        public ReadOnlyJob Second { get; private set; } = null!;

        private Harness()
        {
            Directory.CreateDirectory(_directory);
            Manager = new DataManager(new RelayConfiguration
            {
                UsersPath = Path.Combine(_directory, "users.json"),
                ProjectsPath = Path.Combine(_directory, "projects.json"),
                QueuesPath = Path.Combine(_directory, "queues.json")
            });
        }

        public static async Task<Harness> CreateAsync()
        {
            if (Job.Types.Count == 0) Job.PopulateStatic();
            var h = new Harness();
            h.User = await h.Manager.CreateUser(new User { Name = "Properties test" });
            var project = await h.Manager.CreateProject(h.User);
            var space = await h.Manager.CreateSpace(h.User, project, new Space { RootDirectory = h._directory });
            var view = await h.Manager.CreateView(h.User, space);
            h.First = await h.Manager.CreateJob(h.User, view, new Note().TypeGuid);
            h.Second = await h.Manager.CreateJob(h.User, view, new Note().TypeGuid);
            await h.Manager.UpdateJob(h.User, h.First, j => { j.Alias = "First job"; j.Notes = "first-job-note"; ((Note)j).ProcessingNote = "first-job-parameter"; j.Status = JobStatus.Finished; });
            await h.Manager.UpdateJob(h.User, h.Second, j => { j.Alias = "Second job"; j.Notes = "second-job-note"; ((Note)j).ProcessingNote = "second-job-parameter"; j.Status = JobStatus.Finished; });
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddFluentUIComponents();
            services.AddBlazoredLocalStorage();
            services.AddSingleton(h.Manager);
            var session = new RelaySession(null!, null!, null!, h.Manager, null!);
            await session.SetAuthenticated(true, h.User);
            services.AddSingleton(session);
            var selection = new CardSelectionService(session, h.Manager);
            services.AddSingleton(selection);
            services.AddSingleton(new JobEditorService(h.Manager, session));
            services.AddSingleton(new MenuActionService(h.Manager, session, null!, null!, selection, null!, null!, null!));
            services.AddSingleton<IJSRuntime, NoJsRuntime>();
            services.AddSingleton<IComponentActivator>(h.Activator);
            h._services = services.BuildServiceProvider();
            h.Activator.Services = h._services;
            h.Renderer = new HtmlRenderer(h._services, h._services.GetRequiredService<ILoggerFactory>());
            return h;
        }

        public Task ShowAsync(ReadOnlyJob job) => Renderer.Dispatcher.InvokeAsync(async () =>
        {
            if (_root == null)
                _root = await Renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(Host.Job)] = job }));
            else
                Activator.Host!.Show(job);
        });

        public Task<string> HtmlAsync() => Renderer.Dispatcher.InvokeAsync(() => _root!.Value.ToHtmlString());

        public async ValueTask DisposeAsync()
        {
            if (Renderer != null) await Renderer.DisposeAsync();
            if (_services != null) await _services.DisposeAsync();
            await Manager.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Field<DataRepository>(Manager, "_dataRepository").Dispose();
            Field<UserRepository>(Manager, "_userRepository").Dispose();
            Directory.Delete(_directory, true);
        }

        private static T Field<T>(object target, string name) =>
            (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    }

    private sealed class Host : ComponentBase
    {
        [Parameter] public ReadOnlyJob Job { get; set; } = null!;
        public void Show(ReadOnlyJob job) { Job = job; StateHasChanged(); }
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FluentTooltipProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<JobProperties>(1);
            builder.AddComponentParameter(2, nameof(JobProperties.Jobs), new[] { Job });
            builder.CloseComponent();
        }
    }

    private sealed class RecordingActivator : IComponentActivator
    {
        public IServiceProvider Services { get; set; } = null!;
        public Host? Host { get; private set; }
        public FluentTabs? Tabs { get; private set; }
        public JobProperties? Properties { get; private set; }
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)ActivatorUtilities.CreateInstance(Services, type);
            if (component is Host host) Host = host;
            if (component is FluentTabs tabs) Tabs = tabs;
            if (component is JobProperties properties) Properties = properties;
            return component;
        }
    }

    private sealed class NoJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }
}

using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Refund.Components.Jobs;
using Refund.Configuration;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using ImportMap = Refund.Jobs.Common.Import.ImportMap.ImportMap;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Repositories;
using Refund.Services.Core.Session;

namespace Refund.Tests.Components;

/// <summary>
/// Job line previews render the (host-provided) job card only while open, open on delayed hover or
/// immediate focus, and close on leave, Escape, job changes, disposal and deletion.
/// </summary>
[Collection("JobRegistry")]
public sealed class JobLinePreviewTests
{
    private const string PreviewHost = "job-preview-host";

    [Fact]
    public async Task ClosedLinesRenderNoPreviewContent()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.ShowAsync(harness.Job1);

        Assert.DoesNotContain(PreviewHost, await harness.HtmlAsync());
        Assert.DoesNotContain("fluent-tooltip", await harness.HtmlAsync());
        Assert.Empty(harness.Renderer.Calls);
    }

    [Fact]
    public async Task FocusOpensTheCardImmediatelyAndEscapeClosesIt()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.ShowAsync(harness.Job1);

        await harness.InvokeLineAsync("HandleFocus");
        string html = await harness.HtmlAsync();
        Assert.Contains(PreviewHost, html);
        // The card is rendered for the line's job; re-renders while open may render it again
        Assert.All(harness.Renderer.Calls, c => Assert.Same(harness.Job1, c.Job));
        var call = harness.Renderer.Calls[0];
        Assert.StartsWith("-preview-", call.DomIdSuffix);
        Assert.Contains($"fake-card-{harness.Job1.Id}{call.DomIdSuffix}", html);

        await harness.InvokeLineAsync("HandleKeyDown", new KeyboardEventArgs { Key = "Escape" });
        Assert.DoesNotContain(PreviewHost, await harness.HtmlAsync());
    }

    [Fact]
    public async Task HoverOpensAfterTheDelayAndLeaveCloses()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.ShowAsync(harness.Job1);

        await harness.InvokeLineAsync("HandleMouseEnter");
        Assert.Contains(PreviewHost, await harness.HtmlAsync());

        await harness.InvokeLineAsync("HandleMouseLeave");
        Assert.DoesNotContain(PreviewHost, await harness.HtmlAsync());
    }

    [Fact]
    public async Task LeavingOrReenteringBeforeTheDelayOpensAtMostOnce()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.ShowAsync(harness.Job1);

        var cancelled = await harness.StartLineAsync("HandleMouseEnter");
        await harness.InvokeLineAsync("HandleMouseLeave");
        var reentered = await harness.StartLineAsync("HandleMouseEnter");
        await cancelled;
        Assert.DoesNotContain(PreviewHost, await harness.HtmlAsync());

        await reentered;
        Assert.Contains(PreviewHost, await harness.HtmlAsync());
    }

    [Fact]
    public async Task ChangingTheJobClosesAndCancelsItsPreview()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.ShowAsync(harness.Job1);
        await harness.InvokeLineAsync("HandleFocus");

        await harness.ShowAsync(harness.Job2);
        Assert.DoesNotContain(PreviewHost, await harness.HtmlAsync());

        // A hover for the line's previous job must not open anything once the job changed
        var pending = await harness.StartLineAsync("HandleMouseEnter");
        await harness.ShowAsync(harness.Job1);
        await pending;
        Assert.DoesNotContain(PreviewHost, await harness.HtmlAsync());
        Assert.DoesNotContain(harness.Renderer.Calls, c => c.Job == harness.Job2);

        await harness.InvokeLineAsync("HandleFocus");
        Assert.Contains($"fake-card-{harness.Job1.Id}", await harness.HtmlAsync());
        Assert.Same(harness.Job1, harness.Renderer.Calls[^1].Job);
    }

    [Fact]
    public async Task DisposingTheLineCancelsAPendingHover()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.ShowAsync(harness.Job1);

        var pending = await harness.StartLineAsync("HandleMouseEnter");
        await harness.ShowAsync(null);
        await pending;

        Assert.DoesNotContain(PreviewHost, await harness.HtmlAsync());
        Assert.Empty(harness.Renderer.Calls);
    }

    [Fact]
    public async Task DeletingTheJobClosesItsOpenPreview()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.ShowAsync(harness.Job1);
        await harness.InvokeLineAsync("HandleFocus");
        Assert.Contains(PreviewHost, await harness.HtmlAsync());

        await harness.Manager.DeleteJob(harness.User, harness.Job1);

        // Deletion notifications arrive asynchronously
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((await harness.HtmlAsync()).Contains(PreviewHost) && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.DoesNotContain(PreviewHost, await harness.HtmlAsync());

        // ...and a deleted job doesn't open again
        await harness.InvokeLineAsync("HandleFocus");
        Assert.DoesNotContain(PreviewHost, await harness.HtmlAsync());
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "relay-job-preview-" + Guid.NewGuid().ToString("N"));
        private readonly RecordingActivator _activator = new();
        private ServiceProvider _services = null!;
        private HtmlRenderer _htmlRenderer = null!;
        private HtmlRootComponent? _root;

        public DataManager Manager { get; }
        public RecordingPreviewRenderer Renderer { get; } = new();
        public ReadOnlyUser User { get; private set; } = null!;
        public ReadOnlyJob Job1 { get; private set; } = null!;
        public ReadOnlyJob Job2 { get; private set; } = null!;

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
            JobRegistry.EnsurePopulated();
            var harness = new Harness();
            try
            {
                harness.User = await harness.Manager.CreateUser(new User { Name = "Preview test" });
                var project = await harness.Manager.CreateProject(harness.User);
                var space = await harness.Manager.CreateSpace(harness.User, project, new Space { RootDirectory = harness._directory });
                var view = await harness.Manager.CreateView(harness.User, space);
                harness.Job1 = await harness.Manager.CreateJob(harness.User, view, new ImportMap().TypeGuid);
                harness.Job2 = await harness.Manager.CreateJob(harness.User, view, new ImportMap().TypeGuid);

                var services = new ServiceCollection();
                services.AddLogging();
                services.AddFluentUIComponents();
                services.AddSingleton(harness.Manager);
                services.AddSingleton(new RelaySession(null!, null!, null!, harness.Manager));
                services.AddSingleton<IJobPreviewRenderer>(harness.Renderer);
                services.AddSingleton<IJSRuntime, NoJsRuntime>();
                services.AddSingleton<IComponentActivator>(harness._activator);
                harness._services = services.BuildServiceProvider();
                harness._htmlRenderer = new HtmlRenderer(harness._services, harness._services.GetRequiredService<ILoggerFactory>());
                return harness;
            }
            catch
            {
                await harness.DisposeAsync();
                throw;
            }
        }

        /// <summary>
        /// Shows a line for <paramref name="job"/>, or removes the line when null.
        /// </summary>
        public Task ShowAsync(ReadOnlyJob? job) => _htmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            if (_root == null)
                _root = await _htmlRenderer.RenderComponentAsync<LineHost>(
                    ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(LineHost.Job)] = job }));
            else
                _activator.Host!.Update(job);
        });

        /// <summary>
        /// Invokes a line event handler and waits for it, including any hover delay.
        /// </summary>
        public async Task InvokeLineAsync(string handler, params object[] args) =>
            await await StartLineAsync(handler, args);

        /// <summary>
        /// Invokes a line event handler and returns its still running task.
        /// </summary>
        public async Task<Task> StartLineAsync(string handler, params object[] args)
        {
            Task running = null!;
            await _htmlRenderer.Dispatcher.InvokeAsync(() =>
            {
                running = (Task)typeof(JobLine).GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic)!
                                               .Invoke(_activator.Line, args)!;
            });
            return running;
        }

        public Task<string> HtmlAsync() => _htmlRenderer.Dispatcher.InvokeAsync(() => _root!.Value.ToHtmlString());

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_htmlRenderer != null)
                    await _htmlRenderer.DisposeAsync();
                if (_services != null)
                    await _services.DisposeAsync();
                await Manager.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                Field<DataRepository>(Manager, "_dataRepository").Dispose();
                Field<UserRepository>(Manager, "_userRepository").Dispose();
                Directory.Delete(_directory, recursive: true);
            }
        }

        private static T Field<T>(object target, string name) =>
            (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    }

    /// <summary>
    /// Parent of the line under test, so tests can change its job or remove it.
    /// </summary>
    private sealed class LineHost : ComponentBase
    {
        [Parameter] public ReadOnlyJob? Job { get; set; }

        public void Update(ReadOnlyJob? job)
        {
            Job = job;
            StateHasChanged();
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (Job == null)
                return;

            builder.OpenComponent<JobLine>(0);
            builder.AddComponentParameter(1, nameof(JobLine.Job), Job);
            builder.AddComponentParameter(2, nameof(JobLine.ShowStatus), false);
            builder.CloseComponent();
        }
    }

    private sealed class RecordingPreviewRenderer : IJobPreviewRenderer
    {
        public List<(ReadOnlyJob Job, string DomIdSuffix)> Calls { get; } = new();

        public RenderFragment Render(ReadOnlyJob job, string domIdSuffix)
        {
            Calls.Add((job, domIdSuffix));
            return builder =>
            {
                builder.OpenElement(0, "div");
                builder.AddAttribute(1, "id", $"fake-card-{job.Id}{domIdSuffix}");
                builder.CloseElement();
            };
        }
    }

    private sealed class RecordingActivator : IComponentActivator
    {
        public LineHost? Host { get; private set; }
        public JobLine? Line { get; private set; }

        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is LineHost host) Host = host;
            if (component is JobLine line) Line = line;
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

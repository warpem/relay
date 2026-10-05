using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Refund.Components.TomogramViewer;
using Refund.Jobs.Ts.Picking.TemplateMatch;
using Refund.Services;
using Refund.Services.Core.Session;
using Refund.Tests.Jobs;

namespace Refund.Tests.Components;

[Collection("JobRegistry")]
public class TemplateMatchExpandedViewTests
{
    [Fact]
    public async Task OpenView_FollowsHandednessDecisionAndLoadsAllTenTomograms()
    {
        using var data = new TemplateMatchTestData();
        data.WriteResults(4, false, 5);
        data.WriteResults(4, true, 14);
        await using var view = new ViewHarness();
        await view.OpenAsync(data.Job);
        await view.SelectAsync(0);

        data.WriteDecision(true);
        data.WriteResults(10, true, 14);
        await view.UpdateAsync();

        Assert.Equal(10, view.AllParticles.Count);
        Assert.All(view.AllParticles, t => Assert.Equal(14f, Assert.Single(t.Particles).Score));
        Assert.Equal(14f, Assert.Single(view.SelectedParticles).Score);
        Assert.Equal(10m, view.Histogram.Sum());
        Assert.Equal(1m, view.SelectedHistogram.Sum());

        await view.SelectAsync(9);
        Assert.Equal(14f, Assert.Single(view.SelectedParticles).Score);

        File.Delete(data.StarPath(10, true));
        await view.UpdateAsync();
        Assert.Empty(view.SelectedParticles);
        Assert.Equal(0m, view.SelectedHistogram.Sum());
    }

    [Fact]
    public async Task UpdatedStarFiles_RefreshAlreadySelectedParticlesAndHistograms()
    {
        using var data = new TemplateMatchTestData();
        data.Job.CheckHandN = null;
        data.WriteResults(4, false, 5);
        await using var view = new ViewHarness();
        await view.OpenAsync(data.Job);
        await view.SelectAsync(0);
        Assert.Equal(5f, Assert.Single(view.SelectedParticles).Score);

        data.WriteResults(10, false, 14);
        await view.UpdateAsync();

        Assert.Equal(10, view.AllParticles.Count);
        Assert.Equal(14f, Assert.Single(view.SelectedParticles).Score);
        Assert.Equal(1m, view.SelectedHistogram.Sum());
    }

    private sealed class ViewHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly HtmlRenderer _renderer;
        private readonly RecordingActivator _activator = new();

        public List<(string Name, List<Particle> Particles)> AllParticles => Field<List<(string, List<Particle>)>>("_allTomogramParticles");
        public List<Particle> SelectedParticles => Field<List<Particle>>("_particles");
        public decimal[] Histogram => Field<decimal[]>("_histogramBinsScore");
        public decimal[] SelectedHistogram => Field<decimal[]>("_selectedHistogramBinsScore");

        public ViewHarness()
        {
            JobRegistry.EnsurePopulated();
            var session = new RelaySession(null!, null!, null!, null!);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddFluentUIComponents();
            services.AddSingleton(new ExpandedJobViewService(null!, session, NullLogger<ExpandedJobViewService>.Instance));
            services.AddSingleton(new FileService(NullLogger<FileService>.Instance));
            services.AddSingleton<IJSRuntime, NoJsRuntime>();
            services.AddSingleton<IComponentActivator>(_activator);
            _services = services.BuildServiceProvider();
            _renderer = new HtmlRenderer(_services, _services.GetRequiredService<ILoggerFactory>());
        }

        public Task OpenAsync(TemplateMatch job) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            await _renderer.RenderComponentAsync<TemplateMatchExpandedView>();
            await InvokeAsync("HandleJobChanged", job.AsReadOnly());
        });

        public Task UpdateAsync() => _renderer.Dispatcher.InvokeAsync(() => InvokeAsync("HandleJobUpdated"));
        public Task SelectAsync(int index) => _renderer.Dispatcher.InvokeAsync(() => InvokeAsync("SelectTomogram", index, false));
        private T Field<T>(string name) => (T)typeof(TemplateMatchExpandedView)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_activator.View)!;
        private Task InvokeAsync(string method, params object[] args) => (Task)typeof(TemplateMatchExpandedView)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_activator.View, args)!;

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _services.DisposeAsync();
        }
    }

    private sealed class RecordingActivator : IComponentActivator
    {
        public TemplateMatchExpandedView? View { get; private set; }
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)Activator.CreateInstance(type)!;
            if (component is TemplateMatchExpandedView view) View = view;
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

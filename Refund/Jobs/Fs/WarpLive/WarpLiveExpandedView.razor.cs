using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel.ReadOnly;
using Refund.DataModel;
using Refund.Components;
using Refund.Services.WarpLive;

namespace Refund.Jobs.Fs.WarpLive;

public partial class WarpLiveExpandedView
{
    private static readonly string[] Views = ["Setup", "Monitor", "Picking", "Movies", "Exports"];
    private WarpLiveSession Session;
    private ReadOnlyWarpLiveJob _job;
    private string _view = "Setup", _parameterError, _actionError;
    private readonly string _layoutId = "warp-live-" + Guid.NewGuid().ToString("N");
    private IJSObjectReference _layoutModule;
    private DotNetObjectReference<WarpLiveExpandedView> _layoutSelf;
    private bool _wideScreen, _follow = true, _disposed, _clientReady, _busy;
    private bool _resourcesOpen;
    private int _perDevice, _memoryPerWorker;
    private string _resourceError;
    private bool ResourceDirty => _perDevice != _job.PerDevice || _memoryPerWorker != _job.MemoryPerWorker;
    private int _selected, _queueId = -1, _poolSize = 1, _refreshQueued;
    private WarpLiveSettings _draft, _lastSettings;
    private WarpLiveMovies.ViewState _movieState = new();
    private WarpLiveTrends _trends;
    private WarpLiveTrends.ViewState _trendState = new();
    private WarpLiveTrends.IndexRange _window;
    private string ViewStorageKey => $"relay.warp.{_job.Identity.Key}.view";
    private bool ExecutionActive => Session?.Running == true || _job.Status == JobStatus.Waiting || _job.Status.IsUnsettled();
    private bool Stopping => _job.Status is JobStatus.Aborting or JobStatus.Finalizing;
    private string ExecutionLabel => Stopping ? "Stopping" : Session.Running ? "Processing" : ExecutionActive ? "Starting" : "Stopped";
    private bool Dirty => _draft != Session.Settings;
    private bool InputLate => Session.Running && InputReference.HasValue && DateTime.UtcNow - InputReference.Value > TimeSpan.FromMinutes(3);
    private DateTime? InputReference => Session.LastInputAt > Session.StartedAt ? Session.LastInputAt : Session.StartedAt ?? Session.LastInputAt;
    private WarpLiveMovie Selected => Session.Movies.Count == 0 ? null : Session.Movies[Math.Clamp(_selected, 0, Session.Movies.Count - 1)];
    private IEnumerable<WarpLiveMovie> WindowMovies => Session.Movies.Skip(_window.Start).Take(_window.Count);
    private int WindowResults => WindowMovies.Count(m => m.Done.HasFlag(WarpLiveStage.Ctf) && !m.Failed);
    private int WindowFailed => WindowMovies.Count(m => m.Failed);
    private int WindowPending => WindowMovies.Count(m => !m.Done.HasFlag(WarpLiveStage.Ctf) && !m.Failed);
    private int WindowAccepted => WindowMovies.Count(m => m.Done.HasFlag(WarpLiveStage.Ctf) && Session.Passes(m));
    private int LatestIndex => Session.Movies.LastOrDefault(m => m.Done.HasFlag(WarpLiveStage.Ctf) && !m.Failed)?.Index ?? 0;
    private string ProcessingSummary => ExecutionActive && !Session.Running ? ExecutionLabel.ToLowerInvariant() : !Session.Running ? $"stopped · {Session.Pending:N0} waiting" + (Session.Outdated > 0 ? $" · {Session.Outdated:N0} outdated" : "") : Session.Pending == 0 && Session.Outdated == 0 ? "caught up" : $"{Session.Pending + Session.Outdated:N0} waiting · result {Age(Session.LastResultAt)}";
    private bool HasWorkerQueues => DataManager.GetWarpLiveWorkerQueues().Count > 0;
    private IEnumerable<SelectOption<string>> QueueOptions {
        get {
            var queues = DataManager.GetWarpLiveWorkerQueues();
            return queues.Count == 0
                ? [new SelectOption<string> { Value = "-1", Text = "No GPU queues configured" }]
                : queues.Select(q => new SelectOption<string> { Value = q.Id.ToString(), Text = string.IsNullOrWhiteSpace(q.Alias) ? $"Queue {q.Id}" : q.Alias });
        }
    }
    private static string Age(DateTime? timestamp) { if (!timestamp.HasValue) return "waiting"; var age = DateTime.UtcNow - timestamp.Value; return age.TotalMinutes >= 1 ? $"{(int)age.TotalMinutes} min ago" : $"{Math.Max(0, (int)age.TotalSeconds)} s ago"; }

    protected override Task InitializeViewAsync()
    {
        _job = ExpandedViewService.CurrentJob as ReadOnlyWarpLiveJob;
        if (_job == null) return Task.CompletedTask;
        ExpandedViewService.OnJobUpdated += JobUpdated;
        if (_job.Status is JobStatus.Clearing or JobStatus.Deleted) return Task.CompletedTask;
        Session = DataManager.WarpLiveSessions.GetOrCreate(_job.Identity, _job.Settings);
        _draft = _lastSettings = Session.Settings;
        _queueId = _job.PoolQueueId;
        if (_queueId < 0) _queueId = DataManager.GetWarpLiveWorkerQueues().FirstOrDefault()?.Id ?? -1;
        _poolSize = Math.Max(1, _job.PoolDesiredSize ?? _job.PoolSize);
        _selected = LatestIndex;
        _window = new(Math.Max(0, Session.Movies.Count - 1000), Session.Movies.Count);
        Session.Changed += Updated;
        return Task.CompletedTask;
    }
    private Task JobUpdated()
    {
        if (ExpandedViewService.CurrentJob is ReadOnlyWarpLiveJob job) {
            _job = job; if (ExecutionActive) _resourcesOpen = false; _poolSize = Math.Max(1, job.PoolDesiredSize ?? job.PoolSize);
            if (job.Status is JobStatus.Clearing or JobStatus.Deleted) return _disposed ? Task.CompletedTask : InvokeAsync(StateHasChanged);
            var current = DataManager.WarpLiveSessions.GetOrCreate(job.Identity, job.Settings);
            if (!ReferenceEquals(current, Session)) {
                if (Session != null) Session.Changed -= Updated; Session = current; Session.Changed += Updated;
                _draft = _lastSettings = Session.Settings; _selected = 0;
                _queueId = job.PoolQueueId;
                if (_queueId < 0) _queueId = DataManager.GetWarpLiveWorkerQueues().FirstOrDefault()?.Id ?? -1;
                _movieState = new(); _trendState = new();
                _window = new(0, Session.Movies.Count);
            }
        }
        return _disposed ? Task.CompletedTask : InvokeAsync(StateHasChanged);
    }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_clientReady || Session == null || _disposed) return;
        _clientReady = true;
        try {
            var module = await Js.InvokeAsync<IJSObjectReference>("import", "./_content/Refund/Jobs/Fs/WarpLive/WarpLiveExpandedView.razor.js");
            if (_disposed) { await module.DisposeAsync(); return; }
            _layoutModule = module; _layoutSelf = DotNetObjectReference.Create(this);
            await module.InvokeVoidAsync("observe", _layoutId, _layoutSelf);
            var saved = await Js.InvokeAsync<string>("localStorage.getItem", ViewStorageKey);
            if (Views.Contains(saved)) { _view = saved; StateHasChanged(); }
        } catch (JSDisconnectedException) { } catch (JSException) { }
    }
    private void Updated()
    {
        if (_disposed || Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        _ = InvokeAsync(() => {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (_disposed) return;
            if (_draft == _lastSettings) _draft = Session.Settings;
            _lastSettings = Session.Settings;
            if (_follow) _selected = LatestIndex;
            _selected = Math.Clamp(_selected, 0, Math.Max(0, Session.Movies.Count - 1));
            StateHasChanged();
        });
    }
    private async Task ChangeView(string view) { _view = view; if (_clientReady) try { await Js.InvokeVoidAsync("localStorage.setItem", ViewStorageKey, view); } catch (JSException) { } }
    private Task SelectMovie(int index) { _selected = Math.Clamp(index, 0, Math.Max(0, Session.Movies.Count - 1)); _follow = false; return Task.CompletedTask; }
    private Task OpenMovie(int index) { _selected = index; _follow = false; if (index < _window.Start || index >= _window.End) _trendState.CenterIndex = index; return ChangeView("Monitor"); }
    private void FollowLatest() { _follow = true; _selected = LatestIndex; _trendState.CenterIndex = null; }
    private void DraftChanged(WarpLiveSettings value) { _draft = value; _parameterError = null; }
    private void RevertSettings() { _draft = _lastSettings = Session.Settings; _parameterError = null; }
    private async Task ApplySettings() { _parameterError = WarpLiveSession.ValidateSettings(_draft); if (_parameterError != null) return; await Action(() => DataManager.ApplyWarpLiveSettings(RelaySession.User, _job, _draft)); }
    private async Task Action(Func<Task> action) { _actionError = null; try { await action(); } catch (Exception ex) { _actionError = ex.Message; } }
    // Every shared mutation passes Relay's authenticated job authorization boundary.
    private Task UpdateSetupSession(Action<WarpLiveSession> update) => DataManager.UpdateWarpLiveSession(RelaySession.User, _job, update);
    private Task UpdateSession(Action<WarpLiveSession> update) => Action(() => DataManager.UpdateWarpLiveSession(RelaySession.User, _job, update));
    private void OpenResources() { _perDevice = _job.PerDevice; _memoryPerWorker = _job.MemoryPerWorker; _resourceError = null; _resourcesOpen = !_resourcesOpen; }
    private void RevertResources() { _perDevice = _job.PerDevice; _memoryPerWorker = _job.MemoryPerWorker; _resourceError = null; }
    private async Task SaveResources() {
        if (ExecutionActive || _busy) return;
        _busy = true; _resourceError = null;
        try { await DataManager.ConfigureWarpLiveWorkers(RelaySession.User, _job, _queueId, _poolSize, _perDevice, _memoryPerWorker); _resourcesOpen = false; }
        catch (Exception ex) { _resourceError = ex.Message; }
        finally { _busy = false; }
    }
    private async Task ToggleProcessing() { if (_busy) return; _busy = true; try { await Action(() => ExecutionActive ? DataManager.StopWarpLiveJob(RelaySession.User, _job) : DataManager.StartWarpLiveJob(RelaySession.User, _job, _queueId, _poolSize)); } finally { _busy = false; } }
    private async Task Resize(int size) { if (size < 1 || _busy) return; _busy = true; try { await Action(async () => { await DataManager.ResizeWarpLiveJob(RelaySession.User, _job, size); _poolSize = size; }); } finally { _busy = false; } }
    private Task Export(bool particles) => Action(async () => { await DataManager.ExportWarpLiveJob(RelaySession.User, _job, particles); });
    private void WindowChanged(WarpLiveTrends.IndexRange window) => _window = window;
    private void RangeChanged(WarpLiveTrends.IndexRange? range) { if (range.HasValue) { _follow = false; _selected = Session.Movies.Skip(range.Value.Start).Take(range.Value.Count).FirstOrDefault(m => m.Done.HasFlag(WarpLiveStage.Ctf))?.Index ?? range.Value.Start; } }
    private Task CurateRange(WarpLiveDisposition disposition) { if (!_trendState.RangeStart.HasValue) return Task.CompletedTask; var indices=Enumerable.Range(_trendState.RangeStart.Value, _trendState.RangeEnd.Value - _trendState.RangeStart.Value).ToArray(); return UpdateSession(s => s.SetDisposition(indices, disposition)); }
    private async Task ClearRange() { if (_trends != null) await _trends.ClearRange(); }
    private Task ShowFailures() { _movieState.Query = ""; _movieState.Status = "Failed"; _movieState.PageIndex = 0; _movieState.Selected.Clear(); return ChangeView("Movies"); }
    [JSInvokable] public Task WideScreenChanged(bool wide) { if (_disposed) return Task.CompletedTask; _wideScreen = wide; return InvokeAsync(StateHasChanged); }
    public async ValueTask DisposeAsync() { if (_disposed) return; _disposed = true; if (Session != null) Session.Changed -= Updated; ExpandedViewService.OnJobUpdated -= JobUpdated; if (_layoutModule != null) try { await _layoutModule.InvokeVoidAsync("dispose", _layoutId); await _layoutModule.DisposeAsync(); } catch (JSDisconnectedException) { } catch (ObjectDisposedException) { } _layoutSelf?.Dispose(); }
}

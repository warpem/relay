using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services;
using Refund.Services.Core.DataManager;

namespace Refund.Components.Jobs;

public partial class BasicJobCardContent : ComponentBase, IAsyncDisposable
{
    [Inject] private FileService FileService { get; set; }
    [Inject] private DataManager DataManager { get; set; }
    [Inject] private IJSRuntime JSRuntime { get; set; }

    [Parameter] public ReadOnlyJob Job { get; set; }

    private ReadOnlyJob _previousJob;
    private JobStatus _previousStatus;
    private int _previousVisIteration = -1;
    private string _elementId = $"log-tail-{Guid.NewGuid()}";
    private string _staleElementId;
    private IJSObjectReference _module;
    private bool _isModuleInitialized;

    private bool _showLogTail;
    private bool _logTailNeedsInit;
    private bool _showQueueInfo;
    private string _queueAlias;

    protected override void OnParametersSet()
    {
        bool jobChanged = Job != _previousJob;
        bool displayChanged = !jobChanged && Job != null &&
                              (Job.Status != _previousStatus ||
                               Job.VisAvailableIteration != _previousVisIteration);

        if (!jobChanged && !displayChanged)
            return;

        if (jobChanged)
        {
            _previousJob = Job;
            _staleElementId = _elementId;
            _elementId = $"log-tail-{Guid.NewGuid()}";
        }

        if (Job != null)
        {
            _previousStatus = Job.Status;
            _previousVisIteration = Job.VisAvailableIteration;
        }

        bool wasShowingLogTail = _showLogTail;

        _showLogTail = false;
        _showQueueInfo = false;
        _queueAlias = null;

        if (Job == null || Job.Status == JobStatus.Failed || Job.VisAvailableIteration >= 0)
        {
            if (wasShowingLogTail && !jobChanged)
                _staleElementId = _elementId;
            return;
        }

        if ((Job.Status == JobStatus.Waiting || Job.Status == JobStatus.Staging) && Job.QueueId is > 0)
        {
            var queue = DataManager.FindClusterQueue(Job.QueueId.Value);
            if (queue != null)
            {
                _queueAlias = queue.Alias;
                _showQueueInfo = true;
            }
        }

        if (!_showQueueInfo && Job.Status != JobStatus.Building)
        {
            _showLogTail = true;
            if (!wasShowingLogTail || jobChanged)
                _logTailNeedsInit = true;
        }
        else if (wasShowingLogTail && !jobChanged)
        {
            _staleElementId = _elementId;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _module = await JSRuntime.InvokeAsync<IJSObjectReference>(
                "import",
                "./_content/Refund/Components/Jobs/BasicJobCardContent.razor.js");
            _isModuleInitialized = true;
        }

        if (_isModuleInitialized && _staleElementId != null)
        {
            await _module.InvokeVoidAsync("cleanupLogTail", _staleElementId);
            _staleElementId = null;
        }

        if (_isModuleInitialized && _logTailNeedsInit)
        {
            _logTailNeedsInit = false;
            await StartLogTail();
        }
    }

    private async Task StartLogTail()
    {
        if (_module == null)
            return;

        // Prefer the Relay log file (.relay/log_it{NNNN}.txt) — works for both local and cluster jobs.
        // Fall back to raw stdout for cluster jobs that haven't had logs processed yet.
        string logPath = Job.LogsAvailableIteration >= 0
            ? Job.LogFilePath(Job.LogsAvailableIteration)
            : Path.Combine(Job.DirectoryPath, Job.NameStdOut);

        var url = FileService.GetUrl(logPath);
        var pollInterval = Job.Status.IsUnsettled() ? 3000 : 0;

        await _module.InvokeVoidAsync("initializeLogTail", _elementId, url, pollInterval);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_module != null)
            {
                await _module.InvokeVoidAsync("cleanupLogTail", _elementId);
                await _module.DisposeAsync();
            }
        }
        catch (Exception e) when (e is JSDisconnectedException or OperationCanceledException)
        {
        }
    }
}

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

    private string _elementId = $"log-tail-{Guid.NewGuid()}";
    private string _staleElementId;
    private IJSObjectReference _module;
    private bool _isModuleInitialized;

    private bool _showErrorTail;
    private bool _showLogTail;
    private bool _tailNeedsInit;
    private bool _showQueueInfo;
    private string _queueAlias;
    private FileTail _tail;

    private sealed record FileTail(string Path, int PollInterval, long Version);

    protected override void OnParametersSet()
    {
        _showErrorTail = Job?.Status is JobStatus.Failed or JobStatus.Interrupted;
        _showLogTail = false;
        _showQueueInfo = false;
        _queueAlias = null;

        if (Job is { VisAvailableIteration: < 0, QueueId: > 0 } &&
            Job.Status is JobStatus.Waiting or JobStatus.Staging)
        {
            var queue = DataManager.FindClusterQueue(Job.QueueId.Value);
            if (queue != null)
            {
                _queueAlias = queue.Alias;
                _showQueueInfo = true;
            }
        }

        _showLogTail = Job != null && !_showErrorTail && !_showQueueInfo &&
                       Job.VisAvailableIteration < 0 && Job.Status != JobStatus.Building;
        FileTail tail = null;
        if (_showErrorTail || _showLogTail)
        {
            string path = _showErrorTail
                ? Job.ErrorFilePath
                : Job.LogsAvailableIteration >= 0
                    ? Job.LogFilePath(Job.LogsAvailableIteration)
                    : Path.Combine(Job.DirectoryPath, Job.NameStdOut);
            int interval = Job.Status.IsUnsettled() ? 3000 : 0;
            tail = new FileTail(path, interval, interval == 0 ? Job.UpdateDate.Ticks : 0);
        }

        if (tail == _tail)
            return;

        if (_tail != null)
            _staleElementId = _elementId;
        _tail = tail;
        _tailNeedsInit = tail != null;
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
            await _module.InvokeVoidAsync("cleanupFileTail", _staleElementId);
            _staleElementId = null;
        }

        if (_isModuleInitialized && _tailNeedsInit)
        {
            _tailNeedsInit = false;
            await StartFileTail();
        }
    }

    private async Task StartFileTail()
    {
        if (_module == null || _tail == null)
            return;

        await _module.InvokeVoidAsync(
            "initializeFileTail", _elementId, FileService.GetUrl(_tail.Path), _tail.PollInterval);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_module != null)
            {
                await _module.InvokeVoidAsync("cleanupFileTail", _elementId);
                await _module.DisposeAsync();
            }
        }
        catch (Exception e) when (e is JSDisconnectedException or OperationCanceledException)
        {
        }
    }
}

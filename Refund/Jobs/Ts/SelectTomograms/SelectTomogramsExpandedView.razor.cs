using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.Components.ThumbnailPanel;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.Utils;
using Warp.Tools;
using WarpHelper = Warp.Tools.Helper;
using ProcessingStatus = Refund.Components.ThumbnailPanel.ProcessingStatus;

namespace Refund.Jobs.Ts.SelectTomograms;

public partial class SelectTomogramsExpandedView : IAsyncDisposable
{
    #region Dependencies

    [Inject] private RelaySession Session { get; set; }
    [Inject] private DataManager DataManager { get; set; }
    [Inject] private ExpandedJobViewService ExpandedViewService { get; set; }
    [Inject] private IToastService ToastService { get; set; }
    [Inject] private ILogger<SelectTomogramsExpandedView> Logger { get; set; }

    #endregion

    #region UI Components

    private ThumbnailPanel _tomogramThumbnailPanel;

    #endregion

    #region Data

    private ReadOnlySelectTomograms _job;
    private TomogramSet _tomogramSet;
    private List<WarpTools.MiniJsonTsItem> _processedTomograms = [];
    private List<ThumbnailData> _allTomogramThumbnails = [];
    private ThumbnailData _selectedTomogramThumbnail;
    private string _selectedTomogramPath;

    private int _selectedCount;
    private int _totalCount;

    private bool IsEditingDisabled => (_job?.IsInteractiveFinished ?? false) ||
                                      _job?.Status != JobStatus.Running;

    #endregion

    #region Lifecycle

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();

        ExpandedViewService.OnJobChanged += HandleJobChanged;
        ExpandedViewService.OnJobUpdated += HandleJobUpdated;
        await HandleJobChanged(ExpandedViewService.CurrentJob);
    }

    public async ValueTask DisposeAsync()
    {
        ExpandedViewService.OnJobChanged -= HandleJobChanged;
        ExpandedViewService.OnJobUpdated -= HandleJobUpdated;
    }

    #endregion

    #region Job Event Handlers

    private async Task HandleJobChanged(ReadOnlyJob job)
    {
        if (job is ReadOnlySelectTomograms selectTomograms)
        {
            _job = selectTomograms;
            _processedTomograms = [];
            _allTomogramThumbnails = [];
            _selectedTomogramThumbnail = null;
            _selectedTomogramPath = null;

            // Get the tomogram set from the INPUT port (for thumbnails and viewer paths)
            _tomogramSet = _job.PortsIn[SelectTomograms.PortInTomogramSet].GetSingleResource<TomogramSet>();

            await LoadData();
        }
        else
        {
            _job = null;
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task HandleJobUpdated()
    {
        await LoadData();
        await InvokeAsync(StateHasChanged);
    }

    #endregion

    #region Data Loading

    private async Task LoadData()
    {
        if (_job == null || _tomogramSet == null)
            return;

        try
        {
            // Always read from input TomogramSet's processed items to show all tomograms,
            // including deselected ones (their check state comes from DeselectedTomograms)
            if (File.Exists(_tomogramSet.ProcessedItemsJson))
            {
                var json = await File.ReadAllTextAsync(_tomogramSet.ProcessedItemsJson);
                _processedTomograms = JsonSerializer.Deserialize<List<WarpTools.MiniJsonTsItem>>(json);
            }

            var thumbnails = new List<ThumbnailData>();

            for (var i = 0; i < _processedTomograms.Count; i++)
            {
                var tomogramName = _processedTomograms[i].Path;
                var name = WarpHelper.PathToName(tomogramName);
                bool isDeselected = _job.DeselectedTomograms.Contains(name);

                thumbnails.Add(new ThumbnailData
                {
                    Index = i,
                    ImagePath = _tomogramSet.ToTomogramThumbnailPath(tomogramName),
                    Status = ProcessingStatus.Processed,
                    Check = !isDeselected
                });
            }

            _allTomogramThumbnails = thumbnails;
            _totalCount = _processedTomograms.Count;
            _selectedCount = _totalCount - _job.DeselectedTomograms.Count;

            if (_allTomogramThumbnails.Count > 0 && _selectedTomogramThumbnail == null)
                await SelectTomogram(0, false);

            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading tomogram data for job {JobId}", _job?.Id);
            ToastService.ShowError($"Error loading tomogram data: {ex.Message}");
        }
    }

    #endregion

    #region Tomogram Selection

    private async Task SelectedTomogramThumbnailChanged(ThumbnailData data)
    {
        if (!_allTomogramThumbnails.Contains(data))
            return;

        int index = _allTomogramThumbnails.IndexOf(data);
        await SelectTomogram(index, false);
    }

    private async Task SelectTomogram(int index, bool scrollToItem)
    {
        if (index < 0 || index >= _processedTomograms.Count)
            return;

        _selectedTomogramThumbnail = _allTomogramThumbnails[index];
        var tomogramName = _processedTomograms[index].Path;

        _selectedTomogramPath = _tomogramSet.ToTomogramPath(tomogramName);

        // Prefer deconvolved version if available
        if (_tomogramSet.HasDeconvolution && _tomogramSet.HasDeconvTomograms)
        {
            var deconvPath = _tomogramSet.ToTomogramDeconvPath(tomogramName);
            if (File.Exists(deconvPath))
                _selectedTomogramPath = deconvPath;
        }

        if (scrollToItem && _tomogramThumbnailPanel != null)
            await _tomogramThumbnailPanel.SetSelectedThumbnailAsync(_selectedTomogramThumbnail);

        await InvokeAsync(StateHasChanged);
    }

    #endregion

    #region Check Handling

    private async Task ThumbnailCheckChanged(ThumbnailData data)
    {
        if (data.Check == null || _job == null)
            return;

        var name = WarpHelper.PathToName(_processedTomograms[data.Index].Path);

        await DataManager.UpdateJob(Session.User, _job, originalJob =>
        {
            var job = (SelectTomograms)originalJob;
            if (data.Check.Value)
                job.DeselectedTomograms.Remove(name);
            else
                job.DeselectedTomograms.Add(name);
        });
    }

    private async Task SelectAll()
    {
        if (_job == null)
            return;

        await DataManager.UpdateJob(Session.User, _job, originalJob =>
        {
            ((SelectTomograms)originalJob).DeselectedTomograms.Clear();
        });
    }

    private async Task DeselectAll()
    {
        if (_job == null)
            return;

        await DataManager.UpdateJob(Session.User, _job, originalJob =>
        {
            var job = (SelectTomograms)originalJob;
            foreach (var item in _processedTomograms)
                job.DeselectedTomograms.Add(WarpHelper.PathToName(item.Path));
        });
    }

    #endregion
}

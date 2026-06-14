using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.Jobs.Ts.Selection.SelectParticles;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Warp;

namespace Refund.Jobs.Common.Tools.ThresholdStatistics;

public partial class ThresholdStatisticsExpandedView : IAsyncDisposable
{
    [Inject] private RelaySession Session { get; set; }
    [Inject] private DataManager DataManager { get; set; }
    [Inject] private ExpandedJobViewService ExpandedViewService { get; set; }
    [Inject] private IToastService ToastService { get; set; }
    [Inject] private ILogger<ThresholdStatisticsExpandedView> Logger { get; set; }

    private ReadOnlyThresholdStatistics _job;
    private bool _isLoading;

    // Raw column data loaded from disk
    private Dictionary<string, float[]> _columnData = new();

    // Histogram ranges and bins
    private Dictionary<string, (decimal Min, decimal Max)> _histogramRanges = new();
    private Dictionary<string, decimal[]> _histogramBins = new();
    private Dictionary<string, decimal[]> _filteredHistogramBins = new();

    // Per-column and total counts
    private Dictionary<string, (int Original, int Filtered)> _columnCounts = new();
    private int _totalOriginal;
    private int _totalFiltered;

    private bool IsEditingDisabled => (_job?.IsInteractiveFinished ?? false) ||
                                      _job?.Status != JobStatus.Running;

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

    #region Job Events

    private async Task HandleJobChanged(ReadOnlyJob job)
    {
        if (job is ReadOnlyThresholdStatistics thresholdJob)
        {
            _job = thresholdJob;
            _columnData.Clear();
            _histogramRanges.Clear();
            _histogramBins.Clear();
            _filteredHistogramBins.Clear();
            _columnCounts.Clear();

            await LoadColumnData();
        }
        else
        {
            _job = null;
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task HandleJobUpdated()
    {
        if (_job == null || _columnData.Count == 0)
            return;

        // No disk I/O needed — just recalculate filtered histograms and counts
        RecalculateFilteredData();
        await InvokeAsync(StateHasChanged);
    }

    #endregion

    #region Data Loading

    private async Task LoadColumnData()
    {
        if (_job == null || _job.NumericalColumnNames.Length == 0)
            return;

        var particleSet = _job.PortsIn[ThresholdStatistics.PortInParticles].GetSingleResource<ParticleSet>();
        if (particleSet == null || !particleSet.HasSingleStar)
            return;

        string starPath = particleSet.ParticlesSingleStarPath;
        if (!File.Exists(starPath))
            return;

        _isLoading = true;
        await InvokeAsync(StateHasChanged);

        try
        {
            Star star;
            if (_job.IsMultiTableInput)
                star = new Star(starPath, "particles");
            else
                star = new Star(starPath);

            foreach (string col in _job.NumericalColumnNames)
            {
                float[] values = star.GetFloatRobust(col);
                _columnData[col] = values;

                // Compute range excluding NaN/Inf
                decimal min = decimal.MaxValue;
                decimal max = decimal.MinValue;

                foreach (float v in values)
                {
                    if (float.IsNaN(v) || float.IsInfinity(v))
                        continue;
                    decimal dv = (decimal)v;
                    if (dv < min) min = dv;
                    if (dv > max) max = dv;
                }

                if (min > max)
                {
                    min = 0;
                    max = 1;
                }

                _histogramRanges[col] = (Math.Floor(min), Math.Ceiling(max));

                // Calculate full histogram bins
                _histogramBins[col] = CalculateHistogramBins(
                    values, _histogramRanges[col].Min, _histogramRanges[col].Max);
            }

            // Calculate filtered data
            RecalculateFilteredData();

            _isLoading = false;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading column data for ThresholdStatistics");
            _isLoading = false;
        }
    }

    #endregion

    #region Histogram Calculation

    private void RecalculateFilteredData()
    {
        if (_job == null || _columnData.Count == 0)
            return;

        int rowCount = _columnData.Values.First().Length;

        // Determine which rows pass ALL active filters
        bool[] passingAll = new bool[rowCount];
        Array.Fill(passingAll, true);

        foreach (string col in _job.NumericalColumnNames)
        {
            if (!_job.FilterSettings.Filters.ContainsKey(col) || !_columnData.ContainsKey(col))
                continue;

            var filter = _job.FilterSettings.Filters[col];
            float[] values = _columnData[col];

            for (int i = 0; i < rowCount; i++)
            {
                if (!passingAll[i])
                    continue;

                float v = values[i];
                if (float.IsNaN(v) || float.IsInfinity(v) || !filter.Passes(v))
                    passingAll[i] = false;
            }
        }

        int totalFiltered = 0;
        for (int i = 0; i < rowCount; i++)
            if (passingAll[i])
                totalFiltered++;

        _totalOriginal = rowCount;
        _totalFiltered = totalFiltered;

        // For each column: calculate per-column count and filtered histogram
        foreach (string col in _job.NumericalColumnNames)
        {
            if (!_columnData.ContainsKey(col))
                continue;

            float[] values = _columnData[col];

            // Per-column pass count (just this column's filter)
            int colFiltered = rowCount;
            if (_job.FilterSettings.Filters.ContainsKey(col))
            {
                var filter = _job.FilterSettings.Filters[col];
                colFiltered = 0;
                for (int i = 0; i < rowCount; i++)
                {
                    float v = values[i];
                    if (!float.IsNaN(v) && !float.IsInfinity(v) && filter.Passes(v))
                        colFiltered++;
                }
            }

            _columnCounts[col] = (rowCount, colFiltered);

            // Filtered histogram: bin only rows passing ALL filters
            var range = _histogramRanges.GetValueOrDefault(col, (0, 1));
            _filteredHistogramBins[col] = CalculateHistogramBinsFiltered(
                values, passingAll, range.Min, range.Max);
        }
    }

    private decimal[] CalculateHistogramBins(float[] values, decimal min, decimal max, int binCount = 50)
    {
        if (values.Length == 0)
            return new decimal[binCount];

        if (min == max)
        {
            var result = new decimal[binCount];
            result[0] = values.Length;
            return result;
        }

        var bins = new decimal[binCount];
        decimal binWidth = (max - min) / binCount;

        foreach (float fv in values)
        {
            if (float.IsNaN(fv) || float.IsInfinity(fv))
                continue;

            decimal v = (decimal)fv;
            int binIndex = (int)((v - min) / binWidth);
            if (binIndex >= binCount)
                binIndex = binCount - 1;
            if (binIndex >= 0 && binIndex < binCount)
                bins[binIndex]++;
        }

        return bins;
    }

    private decimal[] CalculateHistogramBinsFiltered(float[] values, bool[] passing, decimal min, decimal max, int binCount = 50)
    {
        if (values.Length == 0)
            return new decimal[binCount];

        if (min == max)
        {
            var result = new decimal[binCount];
            int count = 0;
            for (int i = 0; i < values.Length; i++)
                if (passing[i]) count++;
            result[0] = count;
            return result;
        }

        var bins = new decimal[binCount];
        decimal binWidth = (max - min) / binCount;

        for (int i = 0; i < values.Length; i++)
        {
            if (!passing[i])
                continue;

            float fv = values[i];
            if (float.IsNaN(fv) || float.IsInfinity(fv))
                continue;

            decimal v = (decimal)fv;
            int binIndex = (int)((v - min) / binWidth);
            if (binIndex >= binCount)
                binIndex = binCount - 1;
            if (binIndex >= 0 && binIndex < binCount)
                bins[binIndex]++;
        }

        return bins;
    }

    #endregion

    #region Range Changes

    private async Task OnRangeChanged(string column, (decimal Start, decimal End) range)
    {
        if (_job == null || string.IsNullOrEmpty(column))
            return;

        await DataManager.UpdateJob(Session.User, _job, originalJob =>
        {
            var job = (ThresholdStatistics)originalJob;
            if (job.FilterSettings.Filters.ContainsKey(column))
            {
                job.FilterSettings.Filters[column].Min = range.Start;
                job.FilterSettings.Filters[column].Max = range.End;
            }
        });
    }

    #endregion

    #region Helpers

    private (int Original, int Filtered) GetColumnCounts(string column) =>
        _columnCounts.TryGetValue(column, out var counts) ? counts : (0, 0);

    private decimal[] GetBins(string column) =>
        _histogramBins.GetValueOrDefault(column, Array.Empty<decimal>());

    private decimal[] GetFilteredBins(string column) =>
        _filteredHistogramBins.GetValueOrDefault(column, Array.Empty<decimal>());

    private decimal GetMinRange(string column) =>
        _histogramRanges.GetValueOrDefault(column, (0, 1)).Min;

    private decimal GetMaxRange(string column) =>
        _histogramRanges.GetValueOrDefault(column, (0, 1)).Max;

    private decimal GetSelectedStart(string column)
    {
        if (_job?.FilterSettings?.Filters != null &&
            _job.FilterSettings.Filters.ContainsKey(column))
            return _job.FilterSettings.Filters[column].Min;
        return GetMinRange(column);
    }

    private decimal GetSelectedEnd(string column)
    {
        if (_job?.FilterSettings?.Filters != null &&
            _job.FilterSettings.Filters.ContainsKey(column))
            return _job.FilterSettings.Filters[column].Max;
        return GetMaxRange(column);
    }

    #endregion
}

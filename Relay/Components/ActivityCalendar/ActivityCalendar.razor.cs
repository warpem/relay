using Microsoft.AspNetCore.Components;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;

namespace Relay.Components.ActivityCalendar;

public partial class ActivityCalendar : ComponentBase
{
    [Parameter]
    public required IEnumerable<ReadOnlyJob> Jobs { get; set; }

    private bool _isLoading = true;
    private DateTime _currentMonth;
    private DateTime _previousMonth;
    private Dictionary<DateOnly, List<ReadOnlyJob>> _activityData = new();
    private int _maxActivity;
    private readonly CancellationTokenSource _cts = new();

    protected override async Task OnParametersSetAsync()
    {
        _isLoading = true;
        await InvokeAsync(StateHasChanged);

        try
        {
            await Task.Run(async () =>
            {
                // Initialize dates
                _currentMonth = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
                _previousMonth = _currentMonth.AddMonths(-1);

                // Group jobs by date and calculate max activity in one pass
                _activityData = Jobs
                                .GroupBy(j => DateOnly.FromDateTime(j.UpdateDate.Date))
                                .ToDictionary(g => g.Key, g => g.ToList());

                _maxActivity = _activityData.Count > 0
                                   ? _activityData.Values.Max(jobs => jobs.Count)
                                   : 0;
            }, _cts.Token);
        }
        finally
        {
            _isLoading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private void MoveToPreviousMonth()
    {
        _currentMonth = _currentMonth.AddMonths(-1);
        _previousMonth = _previousMonth.AddMonths(-1);
    }

    private void MoveToNextMonth()
    {
        if (_currentMonth.Month != DateTime.Now.Month || _currentMonth.Year != DateTime.Now.Year)
        {
            _currentMonth = _currentMonth.AddMonths(1);
            _previousMonth = _previousMonth.AddMonths(1);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _cts.Dispose();
    }
}
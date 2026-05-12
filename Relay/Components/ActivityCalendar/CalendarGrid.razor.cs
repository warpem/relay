using System.Globalization;
using Microsoft.AspNetCore.Components;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;

namespace Relay.Components.ActivityCalendar;

public partial class CalendarGrid : ComponentBase
{
    [Inject] private ILogger<CalendarGrid> Logger { get; set; } = default!;
    [Parameter]
    public required DateTime Month { get; set; }

    [Parameter]
    public required Dictionary<DateOnly, List<ReadOnlyJob>> ActivityData { get; set; }

    [Parameter]
    public required int MaxActivity { get; set; }

    private readonly Dictionary<DateTime, string> _dayIds = new();
    private bool _isTooltipVisible;
    private string _currentTooltipId = "";
    private DateTime? _tooltipDate;
    private List<Job> _tooltipJobs;

    private DateTime GetStartDate()
    {
        var firstOfMonth = new DateTime(Month.Year, Month.Month, 1);
        var dayOfWeek = firstOfMonth.DayOfWeek;
        var daysToSubtract = dayOfWeek == DayOfWeek.Sunday ? 6 : ((int)dayOfWeek - 1);
        return firstOfMonth.AddDays(-daysToSubtract);
    }

    private string GetDayCellClass(bool isCurrentMonth, bool isFutureDate)
    {
        var classes = new List<string> { "day-cell" };
        if (!isCurrentMonth) classes.Add("other-month");
        if (isFutureDate) classes.Add("future-date");
        return string.Join(" ", classes);
    }

    private string GetActivityStyle(int activityCount)
    {
        if (activityCount == 0 || MaxActivity == 0) return "";
        
        var intensity = (float)activityCount / MaxActivity;
        var alpha = Math.Max(0.1f, Math.Min(1f, intensity));
        return $"background-color: rgba(0, 120, 212, {alpha:F2})";
    }

    private void OnDayHover(DateTime date, List<Job>? jobs)
    {
        if (!_dayIds.TryGetValue(date, out var id)) return;

        _currentTooltipId = date.ToString(CultureInfo.InvariantCulture);
        _tooltipDate = date;
        _tooltipJobs = jobs;
        _isTooltipVisible = true;
        
        Logger.LogDebug("Date hover: {Date}", date.ToString(CultureInfo.InvariantCulture));
    }

    private void OnDayLeave()
    {
        _isTooltipVisible = false;
    }
}
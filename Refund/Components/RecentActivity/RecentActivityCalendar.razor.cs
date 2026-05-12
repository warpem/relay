using Microsoft.AspNetCore.Components;
using Refund.DataModel;

namespace Refund.Components.RecentActivity;

public partial class RecentActivityCalendar
{
    /// <summary>
    ///     A map of last modified dates to jobs - created off of the Jobs array
    /// </summary>
    private Dictionary<DateTime, List<Job>> _activitiesByDate = null!;

    private DateTime _endDataRange;
    private DateTime _startDataRange;
    private DateTime _startDate;

    /// <summary>
    ///     A list of jobs to use to display activity for
    /// </summary>
    [Parameter]
    public required List<Job> Jobs { get; set; }

    /// <summary>
    ///     The current month being displayed by this component
    /// </summary>
    [Parameter]
    public DateTime Month { get; set; } = new(DateTime.Now.Year, DateTime.Now.Month, 1);

    protected override void OnParametersSet()
    {
        _activitiesByDate = new Dictionary<DateTime, List<Job>>();

        foreach(var jobGrouping in Jobs.GroupBy(x => x.UpdateDate.Date))
        {
            _activitiesByDate.Add(jobGrouping.Key, jobGrouping.ToList());
        }

        SetDateRanges();

        base.OnParametersSet();
    }

    /// <summary>
    ///     Updates the calendars view to the configured month
    /// </summary>
    private void SetDateRanges()
    {
        _startDataRange = Month;
        _endDataRange = _startDataRange.AddMonths(1).AddDays(-1);
        _startDate = _startDataRange.AddDays(-(int)_startDataRange.DayOfWeek);
    }

    /// <summary>
    ///     Gets the text color to use for the given date; Needed to make sure text is visible when background color is
    ///     different
    /// </summary>
    /// <param name="day">Date to evaluate</param>
    /// <returns>color specifier to use in css</returns>
    public string GetTextColor(DateTime day)
    {
        string color;

        if(day < _startDataRange || day > _endDataRange)
        {
            color = "whitesmoke";
        }
        else
        {
            color = _activitiesByDate.ContainsKey(day) ? "white" : "gray";
        }

        return color;
    }

    /// <summary>
    ///     Gets the icon color for the day based on activity level
    /// </summary>
    /// <param name="day">Day to evaluate</param>
    /// <returns>css string that can be added to style to change the color</returns>
    public string GetColor(DateTime day)
    {
        var result = "fill:#E3F2FD";

        if(day < _startDataRange || day > _endDataRange)
        {
            result = "fill:transparent";
        }
        else if(_activitiesByDate.TryGetValue(day, out var activity))
        {
            switch(activity.Count)
            {
                case < 2:
                    result = "fill: #E3F2FD";

                    break;
                case < 3:
                    result = "fill: #2196F3";

                    break;
                default:
                    result = "fill: #0D47A1";

                    break;
            }
        }

        return result;
    }

    private void SetCalendarStartDate()
    {
        Month = Month.AddMonths(-1);
        SetDateRanges();
    }

    private void SetCalendarEndDate()
    {
        Month = Month.AddMonths(1);
        SetDateRanges();
    }
}
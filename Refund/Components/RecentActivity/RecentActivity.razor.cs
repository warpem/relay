using Microsoft.AspNetCore.Components;
using Refund.DataModel;
using Refund.Utils;

namespace Refund.Components.RecentActivity;

public partial class RecentActivity
{
    private Dictionary<DateTime, int> _activityLevel = null!;
    private DateTime _startDataRange;
    private DateTime _startDate;

    /// <summary>
    ///     List of jobs to display the most recent from
    /// </summary>
    [Parameter]
    public required List<Job> Jobs { get; set; }

    /// <summary>
    ///     The number of days to the recent activity from
    /// </summary>
    [Parameter]
    public int Days { get; set; } = 28;

    protected override void OnParametersSet()
    {
        _activityLevel = new Dictionary<DateTime, int>();

        Jobs.Select(x => x.UpdateDate.Date)
            .Distinct()
            .ToList()
            .ForEach(x => { _activityLevel.Add(x, Jobs.Count(y => y.UpdateDate.Date == x)); });

        _startDataRange = DateTime.Now.Date.AddDays(-1 * (Days - 1));
        _startDate = _startDataRange.AddDays(-1 * (int)_startDataRange.DayOfWeek);

        base.OnParametersSet();
    }

    /// <summary>
    ///     Gets a color for the day based on the amount of activity
    /// </summary>
    /// <param name="day">The day to evaluate</param>
    /// <returns>A hex representation of the color to use for the day</returns>
    public string GetColor(DateTime day)
    {
        var result = "fill:#E3F2FD";

        if(day < _startDataRange || day > DateTime.Now)
        {
            result = "fill:transparent";
        }
        else if(_activityLevel.TryGetValue(day, out var activity))
        {
            switch(activity)
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

    private MarkupString TooltipHtmlContent(DateTime currentDate, List<Job> jobs)
    {
        var jobsForDay = jobs
            .Where(j => j.UpdateDate.Date == currentDate.Date)
            .Select(j => j.QualifiedName)
            .ToList();

        return new MarkupString(
            $"{(jobsForDay.Any() ? string.Join("<br />", jobsForDay) : "No jobs")}" +
            $"<br />" +
            $"<span style=\"color:darkgray\">{currentDate:D}</strong>");
    }
}
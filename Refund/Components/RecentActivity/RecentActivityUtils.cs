using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Refund.Components.RecentActivity;

public class RecentActivityUtils
{
    /// <summary>
    ///     Gets the display icon for a given day based on activity level
    /// </summary>
    /// <param name="activitiesByDate">A map of last modified dates to jobs.</param>
    /// <param name="day">Day to evaluate</param>
    /// <returns>An svg icon</returns>
    public static Icon GetFluentIcon<T>(Dictionary<DateTime, T> activitiesByDate, DateTime day) =>
        activitiesByDate.ContainsKey(day)
            ? new Icons.Filled.Size24.Square()
            : new Icons.Regular.Size24.Square();
}
using Microsoft.Extensions.DependencyInjection;
using Refund.Components.SingleAxisScatter;
using Refund.Utils;

namespace Refund.Services;

/// <summary>
/// Service for coordinating highlighting and synchronization between multiple scatter plot components.
/// This service enables highlighting the same data point across multiple scatter plots.
/// </summary>
public class ScatterHighlightService
{
    /// <summary>
    /// Event fired when a highlight changes in any scatter plot.
    /// </summary>
    public event Func<object, int, ScatterPoint?, Task> HighlightChanged;

    /// <summary>
    /// Sets the highlighted point across all scatter plots.
    /// </summary>
    /// <param name="sender">The scatter plot that initiated the highlight</param>
    /// <param name="pointIndex">The index of the highlighted point</param>
    public async Task SetHighlight(object sender, int pointIndex, ScatterPoint? item)
    {
        await HighlightChanged.InvokeAllAsync(sender, pointIndex, item);
    }
}
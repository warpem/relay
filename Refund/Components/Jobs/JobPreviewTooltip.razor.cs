using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel.ReadOnly;
using Refund.Services.Core.DataManager;

namespace Refund.Components.Jobs;

/// <summary>
/// Tooltip showing the full card of a job next to an anchor element. The owner decides when it's
/// open (typically through <see cref="HoverPreview{T}"/>) and passes the job only then; the card is
/// rendered through <see cref="IJobPreviewRenderer"/> and exists only while the tooltip is open.
/// </summary>
public partial class JobPreviewTooltip : ComponentBase, IDisposable
{
    /// <summary>
    /// The job to preview, or null to close the tooltip and render nothing.
    /// </summary>
    [Parameter]
    public ReadOnlyJob Job { get; set; }

    /// <summary>
    /// DOM ID of the element the tooltip is attached to.
    /// </summary>
    [Parameter, EditorRequired]
    public string Anchor { get; set; }

    /// <summary>
    /// Preferred tooltip position; null lets the tooltip pick the best one.
    /// </summary>
    [Parameter]
    public TooltipPosition? Position { get; set; }

    /// <summary>
    /// Suffix that keeps the preview card's DOM IDs unique on the page.
    /// </summary>
    [Parameter, EditorRequired]
    public string DomIdSuffix { get; set; }

    /// <summary>
    /// Invoked when the tooltip asks to be closed: Escape was pressed or the job was deleted.
    /// </summary>
    [Parameter]
    public EventCallback OnDismissed { get; set; }

    [Inject]
    private IJobPreviewRenderer Renderer { get; set; }

    [Inject]
    private DataManager DataManager { get; set; }

    private ReadOnlyJob _job;
    private GroupEventSubscription _deletedSubscription;

    protected override void OnParametersSet()
    {
        if (_job == Job)
            return;

        _deletedSubscription?.Unsubscribe();
        _deletedSubscription = null;
        _job = Job;

        // Only an open preview listens, so lines and dots don't each subscribe just in case
        if (_job?.Space?.Project != null)
        {
            var job = _job;
            _deletedSubscription = DataManager.JobDeleted.Add(GroupName.SpecificJob(job), _ => InvokeAsync(async () =>
            {
                if (_job == job)
                    await OnDismissed.InvokeAsync();
            }));
        }
    }

    private Task HandleDismissed() => OnDismissed.InvokeAsync();

    public void Dispose()
    {
        _deletedSubscription?.Unsubscribe();
        _deletedSubscription = null;
        _job = null;
    }
}

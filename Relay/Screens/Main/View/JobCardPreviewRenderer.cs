using Microsoft.AspNetCore.Components;
using Refund.Components.Jobs;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;

namespace Relay.Screens.Main.View;

/// <summary>
/// Renders job previews as the full <see cref="JobCard"/> in overview mode, so previews look like
/// the job's card in its view but can't be dragged, connected or used to change the selection.
/// </summary>
public sealed class JobCardPreviewRenderer : IJobPreviewRenderer
{
    public RenderFragment Render(ReadOnlyJob job, string domIdSuffix) => builder =>
    {
        builder.OpenComponent<JobCard>(0);
        builder.AddComponentParameter(1, nameof(JobCard.Job), job);
        builder.AddComponentParameter(2, nameof(JobCard.OverviewMode), true);
        builder.AddComponentParameter(3, nameof(JobCard.SelectionKey), (SelectionKey?)SelectionKey.ForJob(job));
        builder.AddComponentParameter(4, nameof(JobCard.DomIdSuffix), domIdSuffix);
        builder.CloseComponent();
    };
}

using Microsoft.AspNetCore.Components;
using Refund.DataModel.ReadOnly;

namespace Refund.Components.Jobs;

/// <summary>
/// Renders the content of a job preview. Implemented by the host application, which owns the full
/// job card that Refund components can't reference directly.
/// </summary>
public interface IJobPreviewRenderer
{
    /// <summary>
    /// Renders a read-only preview of <paramref name="job"/>.
    /// </summary>
    /// <param name="job">The job to preview</param>
    /// <param name="domIdSuffix">Appended to the preview's DOM IDs so they don't collide with the
    /// job's regular card or with other previews on the same page</param>
    RenderFragment Render(ReadOnlyJob job, string domIdSuffix);
}

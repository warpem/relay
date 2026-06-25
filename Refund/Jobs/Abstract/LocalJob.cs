using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.UIFields;
using Refund.Utils;

namespace Refund.Jobs;

[GenerateReadOnly]
public abstract class LocalJob : Job
{
    /// <summary>
    /// Tracks the progress of log generation for this job.
    /// Used to notify the UI when logs become available.
    /// </summary>
    /// <returns>An action to execute when logs become available, or null if no update is needed</returns>
    public override Action TrackProgressLogs()
    {
        var baseResult = base.TrackProgressLogs();

        if (LogsAvailableIteration < 0)
            return () =>
            {
                baseResult?.Invoke();
                LogsAvailableIteration = 0;
            };

        return baseResult;
    }
}

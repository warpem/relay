using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.UIFields;
using Warp.Tools;

namespace Refund.Jobs.Common.Notes.Vibe;

/// <summary>
/// A documentation job that allows users to add an emoji-based mood or "vibe" indicator
/// within a workflow. This job type serves as an emotional annotation tool, enabling users
/// to mark how they feel about the processing at a specific point in the workflow.
/// 
/// Like other Notes jobs, Vibe jobs are used throughout the system for maintaining emotional
/// context in workflows. They can be used to mark particularly successful or challenging
/// processing stages, providing visual cues to other users about workflow pain points or triumphs.
/// </summary>
[GenerateReadOnly]
public class Vibe : Job, ILocalJob
{
    public override string TypeGuid => "73cea40a-c8d6-42f1-bae3-357851cda480";

    /// <summary>
    /// Defines the size of the job card in the workflow view.
    /// Vibe uses a 1x1 square as it's a simple annotation job.
    /// 
    /// This property is accessed by the ReadOnlyJob wrapper to determine the visual space
    /// the job occupies in workflow layouts.
    /// </summary>
    public override int2 CardSquareCount { set; get; } = new int2(1, 1);

    /// <summary>
    /// The category path for job type selection in the UI.
    /// 
    /// Used by the job creation system to identify and instantiate this job type.
    /// For example, this category is used when another component calls:
    /// <code>await _dataManager.CreateJob(_session.User, view, jobType.TypeCategory, original);</code>
    /// or when cloning jobs:
    /// <code>clone = space.CreateJob(original.TypeCategory, original, view);</code>
    /// </summary>
    public override string TypeCategory => "Common.Notes.Vibe";

    /// <summary>
    /// The full display name of this job type.
    /// 
    /// This is used for both UI display and job identification. It's referenced
    /// when displaying job names in the UI and when creating qualified job names
    /// in the format "J{Id}: {TypeName}" when no alias is provided.
    /// </summary>
    public override string TypeName => "Vibe";

    /// <summary>
    /// The abbreviated name used in space-constrained UI elements.
    /// 
    /// Accessed via the ReadOnlyJob wrapper to provide consistent access
    /// to job metadata in the UI.
    /// </summary>
    public override string TypeNameShort => "Vibe";

    /// <summary>
    /// A brief description of the job's purpose.
    /// 
    /// Accessed via the ReadOnlyJob wrapper to provide consistent access
    /// to job metadata in the UI.
    /// </summary>
    public override string TypeDescription => "Notes the vibe of this point in processing";

    /// <summary>
    /// Specifies that this job runs on the local queue rather than a cluster.
    /// Since it's a simple annotation job, it only writes an emoji to a log file.
    /// 
    /// This property is accessed by the ReadOnlyJob wrapper to determine which
    /// queue system should process the job.
    /// </summary>
    public override JobQueueType QueueType => JobQueueType.Local;

    /// <summary>
    /// Indicates that this job doesn't support multiple iterations.
    /// 
    /// This flag is used by the ReadOnlyJob wrapper to determine if the job
    /// supports running multiple iterations for progressive refinement.
    /// </summary>
    public override bool IsIterative => false;

    /// <summary>
    /// The component type used to render this job as a card in the workflow view.
    /// 
    /// This type reference is accessed via the ReadOnlyJob wrapper to dynamically
    /// instantiate the appropriate view component for this job type. The VibeCardContent
    /// component is specifically designed to display the emoji in a visually appealing way.
    /// </summary>
    public override Type CardViewType => typeof(VibeCardContent);

    /// <summary>
    /// The component type used for the expanded detailed view of this job.
    /// 
    /// This type reference is accessed via the ReadOnlyJob wrapper to dynamically
    /// instantiate the appropriate expanded view component for this job type.
    /// The VibeExpandedView component is a simple view that displays a large version
    /// of the selected emoji using FluentEmoji rendering.
    /// </summary>
    public override Type ExpandedViewType => typeof(VibeExpandedView);


    #region Parameters

    /// <summary>
    /// The emoji representing the user's emotional response or "vibe" at this point in processing.
    /// This is the primary data stored by this job type.
    /// 
    /// This property uses the UiEmoji field type which connects to the emoji picker component,
    /// allowing users to select from a wide range of standardized emojis.
    /// </summary>
    [UiFieldGroup("Parameters", 0)]
    [UiEmoji("", "Vibe")]
    [RelayProperty]
    public string ProcessingVibe { get; set; } = "🙂";

    #endregion

    /// <summary>
    /// Initializes a new instance of the Vibe job type.
    /// Creates empty port collections as this job doesn't have any input or output connections.
    /// </summary>
    public Vibe()
    {
        PortsIn = new(new Dictionary<string, PortIn>());
        PortsOut = new(new Dictionary<string, PortOut>());
    }

    /// <summary>
    /// Validates the job inputs before running.
    /// For the Vibe job, there's no validation needed since it doesn't process any data.
    /// 
    /// This method is called through the ReadOnlyJob wrapper to validate job inputs
    /// before execution. It's part of the standard job lifecycle.
    /// </summary>
    /// <returns>An empty dictionary, indicating no validation errors.</returns>
    public override Dictionary<string, string> ValidateInputs()
    {
        var errors = new Dictionary<string, string>();
        return errors;
    }

    /// <summary>
    /// Executes the job on the local machine.
    /// Creates a log file and writes the selected emoji into it.
    /// </summary>
    /// <param name="token">Cancellation token to abort the job if needed.</param>
    public void RunLocal(CancellationToken token)
    {
        Directory.CreateDirectory(RelayResultsDirectoryPath);

        using (TextWriter logger = File.CreateText(LogFilePath(0)))
        {
            (logger as StreamWriter).AutoFlush = true;
            
            try
            {
                logger.WriteLine("Detecting the vibe...");
                logger.WriteLine($"Vibe detected successfully: {ProcessingVibe}");
            }
            catch (Exception exc)
            {
                logger.WriteLine($"An error occurred: {exc.Message}");
                throw;
            }
        }
    }

    /// <summary>
    /// Provides a delegate to track the progress of log generation.
    /// For Vibe jobs, this simply sets LogsAvailableIteration to 0 when logs are ready.
    /// 
    /// This method is called by the QueueRepository to update job status and trigger UI updates.
    /// It's called both during normal job execution monitoring and during final cleanup
    /// when a job completes. Similar to other Notes jobs, the tracking is very simple.
    /// </summary>
    /// <returns>Action delegate to update progress state, or null if already updated.</returns>
    public override Action TrackProgressLogs()
    {
        if (LogsAvailableIteration < 0)
            return () =>
            {
                LogsAvailableIteration = 0;
            };
        
        return null;
    }

    /// <summary>
    /// Provides a delegate to track the progress of result visualization.
    /// For Vibe jobs, this simply sets VisAvailableIteration to 0 when visualization is ready.
    /// 
    /// This method is called by the QueueRepository to update job result visualization status
    /// and trigger UI updates. It's called both during normal job execution monitoring and 
    /// during final cleanup when a job completes, following the same pattern as other Jobs
    /// for consistent UI behavior.
    /// </summary>
    /// <returns>Action delegate to update visualization state, or null if already updated.</returns>
    public override Action TrackProgressResults()
    {
        if (VisAvailableIteration < 0)
            return () =>
            {
                VisAvailableIteration = 0;
            };
        
        return null;
    }
}
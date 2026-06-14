using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.UIFields;
using Warp.Tools;

namespace Refund.Jobs.Common.Notes.Note;

/// <summary>
/// A documentation job that allows users to add text notes within a workflow.
/// This job type serves as a documentation tool, enabling users to annotate their
/// workflow with textual information without affecting data processing.
/// 
/// Notes jobs are used extensively throughout the system for maintaining workflow context
/// and are a cornerstone of the user documentation process. They're often created programmatically
/// when cloning jobs, creating new spaces, or adding comments to processing stages.
/// </summary>
[GenerateReadOnly]
public class Note : Job, ILocalJob
{
    public override string TypeGuid => "9d901fae-9b1a-413b-a08e-1fccab63267a";

    /// <summary>
    /// Defines the size of the job card in the workflow view. 
    /// Note uses a 1x1 square as it's a simple documentation job.
    /// 
    /// This property is used by the ReadOnlyJob wrapper to determine the visual space
    /// the job occupies in workflow layouts.
    /// </summary>
    public override int2 CardSquareCount { set; get; } = new int2(1, 1);

    /// <summary>
    /// The category path for job type selection in the UI.
    /// 
    /// Used by the job creation system to identify and instantiate this job type.
    /// For example, this category is used when another component calls:
    /// <code>await _dataManager.CreateJob(_session.User, view, jobType.TypeCategory, original);</code>
    /// </summary>
    public override string TypeCategory => "Common.Notes.Note";

    /// <summary>
    /// The full display name of this job type.
    /// 
    /// This is used for both UI display and job identification. It's referenced
    /// when displaying job names in the UI and when creating qualified job names
    /// in the format "J{Id}: {TypeName}" when no alias is provided.
    /// </summary>
    public override string TypeName => "Note";

    /// <summary>
    /// The abbreviated name used in space-constrained UI elements.
    /// 
    /// Accessed via the ReadOnlyJob wrapper to provide consistent access
    /// to job metadata in the UI.
    /// </summary>
    public override string TypeNameShort => "Note";

    /// <summary>
    /// A brief description of the job's purpose.
    /// 
    /// Accessed via the ReadOnlyJob wrapper to provide consistent access
    /// to job metadata in the UI.
    /// </summary>
    public override string TypeDescription => "Notes your thoughts at this point in processing";

    /// <summary>
    /// Specifies that this job runs on the local queue rather than a cluster.
    /// Since it's a simple documentation job, it only writes a note to a log file.
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
    /// instantiate the appropriate view component for this job type.
    /// </summary>
    public override Type CardViewType => typeof(NoteCardContent);

    /// <summary>
    /// The component type used for the expanded detailed view of this job.
    /// 
    /// This type reference is accessed via the ReadOnlyJob wrapper to dynamically
    /// instantiate the appropriate expanded view component for this job type.
    /// </summary>
    public override Type ExpandedViewType => typeof(NoteExpandedView);


    #region Parameters

    /// <summary>
    /// The main content of the note - free-form text entered by the user.
    /// This is the primary data stored by this job type.
    /// </summary>
    [UiFieldGroup("Parameters", 0)]
    [UiText("", "Note text")]
    [RelayProperty]
    public string ProcessingNote { get; set; } = "";

    #endregion

    /// <summary>
    /// Initializes a new instance of the Note job type.
    /// Creates empty port collections as this job doesn't have any input or output connections.
    /// </summary>
    public Note()
    {
        PortsIn = new(new Dictionary<string, PortIn>());
        PortsOut = new(new Dictionary<string, PortOut>());
    }

    /// <summary>
    /// Validates the job inputs before running.
    /// For the Note job, there's no validation needed since it doesn't process any data.
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
    /// Creates a log file and writes the note text into it.
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
                logger.WriteLine("Writing down the notes...");
                logger.WriteLine($"Success: {ProcessingNote}");
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
    /// For Note jobs, this simply sets LogsAvailableIteration to 0 when logs are ready.
    /// 
    /// This method is called by the QueueRepository to update job status and trigger UI updates.
    /// It's called both during normal job execution monitoring and during final cleanup
    /// when a job completes.
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
    /// For Note jobs, this simply sets VisAvailableIteration to 0 when visualization is ready.
    /// 
    /// This method is called by the QueueRepository to update job result visualization status
    /// and trigger UI updates. It's called both during normal job execution monitoring and 
    /// during final cleanup when a job completes.
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
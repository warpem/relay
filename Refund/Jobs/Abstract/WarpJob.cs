using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.UIFields;
using Refund.Utils;

namespace Refund.Jobs;

/// <summary>
/// Base class for all jobs that utilize the Warp software for cryo-EM data processing.
/// Warp is a software package for real-time evaluation, pre-processing, and initial reconstruction
/// of cryo-EM data.
/// </summary>
/// <remarks>
/// This class extends the base Job class with Warp-specific module requirements.
/// </remarks>
[GenerateReadOnly]
public abstract class WarpJob : Job, IItemProgress
{
    /// <summary>
    /// Number of items processed by the job so far (null until the job starts reporting).
    /// </summary>
    [RelayProperty]
    [Clearable]
    public int? NItemsProcessed { get; set; }

    /// <summary>
    /// Number of items that failed processing (null until the job starts reporting).
    /// </summary>
    [RelayProperty]
    [Clearable]
    public int? NItemsFailed { get; set; }

    /// <summary>
    /// Total number of items to process (null until the job starts reporting).
    /// </summary>
    [RelayProperty]
    [Clearable]
    public int? NItemsTotal { get; set; }

    protected WarpJob()
    {
        MemoryPerWorker = DefaultMemoryPerWorker;
    }

    public override Dictionary<string, string> ComposeCommandArguments()
    {
        var result = base.ComposeCommandArguments();
        result["strict"] = "";

        return result;
    }

    #region GPU options

    protected virtual int DefaultMemoryPerWorker => 12;

    [UiFieldGroup("Resources", 999)]
    [UiInt("", "Memory per worker",
           min: 1,
           unit: "GB",
           helpText: "Amount of memory to request for each worker process.")]
    [RelayProperty]
    public virtual int MemoryPerWorker { get; set; }

    #endregion

    /// <summary>
    /// Gets the modules that this job can utilize if available.
    /// Includes the base supported modules plus "warp".
    /// </summary>
    public override string[] SupportedModules => base.SupportedModules.Concat(["warp"]).ToArray();

    /// <summary>
    /// Gets the modules that must be available for this job to run.
    /// Includes the base required modules plus "warp".
    /// </summary>
    public override string[] RequiredModules => base.RequiredModules.Concat(["warp"]).ToArray();

    /// <summary>
    /// Gets a command suffix that creates a success marker file when the job completes successfully.
    /// </summary>
    public override string CommandSuffix => $" && touch {PathSuccess}";

    #region Standard result files

    public virtual string ResProcessedItemsJson => Path.Combine(DirectoryPath, "processed_items.json");

    public virtual string ResFailedItemsJson => Path.Combine(DirectoryPath, "failed_items.json");

    #endregion

    /// <summary>
    /// Monitors and processes log output from the running job.
    /// WarpTools have a standardized progress line format.
    /// </summary>
    /// <returns>An action to update progress state, or null if no update is needed.</returns>
    public override Action TrackProgressLogs()
    {
        var baseResult = base.TrackProgressLogs();

        if (!File.Exists(PathStdOut))
            return baseResult;

        // Read log file
        string[] logLines = File.ReadAllText(PathStdOut).Split('\n');

        if (logLines.Length == 0)
            return null;

        // Clean log lines to remove progress bar characters
        logLines = JobTools.CleanProgressBarLines(logLines);

        // Parse progress information
        WarpTools.ExtractProgressInfo(logLines,
                                      out int itemsProcessed,
                                      out int itemsTotal,
                                      out int itemsFailed,
                                      out string remainingTime);

        // Save processed logs
        JobTools.WriteLogFile(string.Join('\n', logLines), LogFilePath(0));

        // Parse remaining time string and update TimeRemaining property
        TimeSpan? timeRemaining = WarpTools.ParseRemainingTimeToCompletion(remainingTime);

        // Return update action if needed
        if (LogsAvailableIteration < 0 ||
            NItemsProcessed != itemsProcessed ||
            NItemsTotal != itemsTotal ||
            NItemsFailed != itemsFailed ||
            TimeRemaining != timeRemaining)
            return () =>
            {
                baseResult?.Invoke();

                NItemsProcessed = itemsProcessed;
                NItemsTotal = itemsTotal;
                NItemsFailed = itemsFailed;
                TimeRemaining = timeRemaining;
                LogsAvailableIteration = 0;
            };

        return baseResult;
    }
}

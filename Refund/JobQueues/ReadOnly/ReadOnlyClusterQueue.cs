using System.Collections.ObjectModel;
using Refund.DataModel.ReadOnly;

namespace Refund.JobQueues.ReadOnly;

/// <summary>
/// Provides a read-only view of a ClusterQueue.
/// Decorates a ClusterQueue to prevent any modifications and expose only getters.
/// </summary>
[ReadOnlyFor(typeof(ClusterQueue))]
public sealed class ReadOnlyClusterQueue : ReadOnlyJobQueue
{
    private readonly ClusterQueue _queue;

    /// <summary>
    /// Initializes a new instance of the ReadOnlyClusterQueue class.
    /// </summary>
    /// <param name="queue">The ClusterQueue to wrap</param>
    internal ReadOnlyClusterQueue(ClusterQueue queue) : base(queue)
    {
        _queue = queue;
    }

    /// <summary>
    /// Gets the custom shell executable path for running cluster commands.
    /// </summary>
    public string CustomShell => _queue.CustomShell;

    /// <summary>
    /// Gets the arguments to pass to the custom shell when executing cluster commands.
    /// </summary>
    public string CustomShellArguments => _queue.CustomShellArguments;

    /// <summary>
    /// Gets the template for executing any command on the cluster.
    /// </summary>
    public string SendCommmandTemplate => _queue.SendCommmandTemplate;

    /// <summary>
    /// Gets the template for submitting a job to the cluster scheduler.
    /// </summary>
    public string SubmitJobTemplate => _queue.SubmitJobTemplate;

    /// <summary>
    /// Gets the template for checking the status of a job on the cluster.
    /// </summary>
    public string StatusJobTemplate => _queue.StatusJobTemplate;

    /// <summary>
    /// Gets the template for aborting/canceling a job on the cluster.
    /// </summary>
    public string AbortJobTemplate => _queue.AbortJobTemplate;

    /// <summary>
    /// Gets the regular expression for extracting the job ID from the cluster scheduler output.
    /// </summary>
    public string JobIdParseRegex => _queue.JobIdParseRegex;

    /// <summary>
    /// Gets the string pattern that indicates a job is pending in the cluster queue.
    /// </summary>
    public string JobStatusParseTemplatePending => _queue.JobStatusParseTemplatePending;

    /// <summary>
    /// Gets the string pattern that indicates a job is running on the cluster.
    /// </summary>
    public string JobStatusParseTemplateRunning => _queue.JobStatusParseTemplateRunning;

    /// <summary>
    /// Gets the string pattern that indicates a job has failed on the cluster.
    /// </summary>
    public string JobStatusParseTemplateFailed => _queue.JobStatusParseTemplateFailed;

    /// <summary>
    /// Gets the template for generating the job submission script sent to the cluster.
    /// </summary>
    public string SubmissionScriptTemplate => _queue.SubmissionScriptTemplate;

    /// <summary>Gets the command template for listing all active job IDs.</summary>
    public string ListJobsTemplate => _queue.ListJobsTemplate;

    /// <summary>Gets the command template for cancelling multiple jobs at once.</summary>
    public string CancelManyJobsTemplate => _queue.CancelManyJobsTemplate;

    /// <summary>
    /// Gets the custom variables that can be used in the submission script template.
    /// Returns a read-only view of the dictionary to prevent modifications.
    /// </summary>
    public ReadOnlyDictionary<string, (string description, string defaultValue)> CustomVariables =>
        new(_queue.CustomVariables);
}
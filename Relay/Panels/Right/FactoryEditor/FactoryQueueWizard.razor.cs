using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;

namespace Relay.Panels.Right.FactoryEditor;

/// <summary>
/// A component that allows users to assign queues to each sub-job in a factory instance
/// before running it. Local jobs are auto-assigned; cluster jobs get a dropdown of
/// compatible queues.
/// </summary>
public partial class FactoryQueueWizard : ComponentBase
{
    /// <summary>
    /// The factory instance whose sub-jobs need queue assignments.
    /// </summary>
    [Parameter]
    public ReadOnlyFactoryInstance Instance { get; set; }

    /// <summary>
    /// Callback invoked when the user cancels the wizard.
    /// </summary>
    [Parameter]
    public EventCallback OnClose { get; set; }

    /// <summary>
    /// Callback invoked after the factory instance has been successfully queued.
    /// </summary>
    [Parameter]
    public EventCallback OnQueued { get; set; }

    [Inject]
    private DataManager DataManager { get; set; }

    [Inject]
    private RelaySession Session { get; set; }

    [Inject]
    private IToastService ToastService { get; set; }

    /// <summary>
    /// Maps real sub-job ID to assigned queue ID. Null means no queue assigned yet.
    /// </summary>
    private Dictionary<int, int?> _assignments = new();

    private bool _isSubmitting;

    protected override void OnParametersSet()
    {
        if (Instance == null) return;
        _assignments.Clear();

        var def = Instance.Definition;
        var subJobs = Instance.SubJobs?.ToList() ?? new();

        foreach (var (idx, subJob) in subJobs.Select((j, i) => (i, j)))
        {
            if (subJob == null || subJob.Status != JobStatus.Building) continue;

            int blueprintId = idx + 1; // Blueprint IDs are 1-based

            // Auto-assign local jobs
            if (typeof(ILocalJob).IsAssignableFrom(subJob.GetOriginalType()))
            {
                _assignments[subJob.Id] = DataManager.LocalQueue.Id;
                continue;
            }

            // Try definition's queue pre-selections first
            if (def?.QueueAssignments.TryGetValue(blueprintId, out var preselectedQueue) == true
                && preselectedQueue.HasValue)
            {
                _assignments[subJob.Id] = preselectedQueue.Value;
            }
            else
            {
                // Fall back to first compatible cluster queue
                var compatible = DataManager.ClusterQueues
                    .FirstOrDefault(q => (q.QueueType & subJob.QueueType) != 0);
                _assignments[subJob.Id] = compatible?.Id;
            }
        }
    }

    /// <summary>
    /// Whether any sub-job has no queue assigned.
    /// </summary>
    private bool HasUnassigned => _assignments.Any(kvp => kvp.Value == null);

    /// <summary>
    /// Whether the confirm button should be enabled.
    /// </summary>
    private bool CanConfirm => !HasUnassigned && !_isSubmitting;

    /// <summary>
    /// Returns true if the job implements ILocalJob and should be auto-assigned to the local queue.
    /// </summary>
    private bool IsLocalJob(ReadOnlyJob job) =>
        typeof(ILocalJob).IsAssignableFrom(job.GetOriginalType());

    /// <summary>
    /// Returns cluster queues whose QueueType flag overlaps with the job's QueueType.
    /// </summary>
    private IEnumerable<ReadOnlyJobQueue> GetCompatibleQueues(ReadOnlyJob job)
    {
        return DataManager.ClusterQueues.Where(q => (q.QueueType & job.QueueType) != 0);
    }

    /// <summary>
    /// Handles queue selection change for a specific sub-job.
    /// </summary>
    private void HandleQueueChanged(int jobId, string value)
    {
        if (int.TryParse(value, out var queueId))
            _assignments[jobId] = queueId;
        else
            _assignments[jobId] = null;
    }

    /// <summary>
    /// Submits all queue assignments and runs the factory instance.
    /// </summary>
    private async Task HandleConfirm()
    {
        _isSubmitting = true;
        try
        {
            // Convert to Dictionary<int, int> (non-nullable) for DataManager
            var assignments = _assignments
                .Where(kvp => kvp.Value.HasValue)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!.Value);

            await DataManager.RunFactoryInstance(Session.User, Instance, assignments);
            await OnQueued.InvokeAsync();
        }
        catch (Exception exc)
        {
            ToastService.ShowError($"Couldn't queue factory: {exc.Message}");
            _isSubmitting = false;
        }
    }
}

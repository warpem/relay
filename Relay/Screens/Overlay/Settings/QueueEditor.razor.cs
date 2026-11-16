using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.Components.CodeEditor;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobQueues;
using Refund.JobQueues.ReadOnly;
using Refund.Services.Core.DataManager;

namespace Relay.Screens.Overlay.Settings;

public partial class QueueEditor : IDisposable
{
    [Inject]
    private INotificationService ToastService { get; set; }

    private ReadOnlyClusterQueue _selectedQueue;
    /// <summary>Subscriptions for the whole queue list, held for the component's lifetime.</summary>
    private readonly List<GroupEventSubscription> _listSubscriptions = new();
    /// <summary>Subscriptions for the selected queue, replaced on every selection.</summary>
    private readonly List<GroupEventSubscription> _subscriptions = new();
    
    private bool CanMoveQueueUp => _selectedQueue != null && 
                                  DataManager.ClusterQueues.IndexOf(_selectedQueue) > 0;
    
    private bool CanMoveQueueDown => _selectedQueue != null && 
                                    DataManager.ClusterQueues.IndexOf(_selectedQueue) < DataManager.ClusterQueues.Count - 1;

    private IEnumerable<JobQueueType> QueueTypes => Enum.GetValues<JobQueueType>().Where(v => v != JobQueueType.Local);

    private IEnumerable<ClusterScheduler> Schedulers => Enum.GetValues<ClusterScheduler>();

    /// <summary>
    /// The job ID regular expression and job status patterns are only consulted for
    /// <see cref="ClusterScheduler.Custom"/> queues; every other scheduler has a built-in parser.
    /// </summary>
    private bool ShowCustomParsingFields => _selectedQueue?.SchedulerType == ClusterScheduler.Custom;

    /// <summary>
    /// A managed queue has no external scheduler to talk to, so the command templates are
    /// meaningless for it and its resource limits take their place.
    /// </summary>
    private bool IsManagedQueue => _selectedQueue?.SchedulerType == ClusterScheduler.Managed;

    /// <summary>Command templates apply to every scheduler except Managed.</summary>
    private bool ShowCommandTemplates => !IsManagedQueue;

    private CodeEditor _submissionScriptEditor;
    private int _submissionScriptCursorPosition = -1;

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();

        // Queue notifications arrive on thread-pool threads; every handler below hops onto the
        // renderer's dispatcher before touching component state, so a render never sees
        // _selectedQueue change underneath it.
        _listSubscriptions.Add(DataManager.QueueCreated.Add(GroupName.Queue(null), HandleQueueListChanged));
        _listSubscriptions.Add(DataManager.QueueDeleted.Add(GroupName.Queue(null), HandleQueueDeleted));

        SelectQueue(DataManager.ClusterQueues.FirstOrDefault(q => q is ReadOnlyClusterQueue));
    }

    /// <summary>Must run on the renderer's dispatcher (event handler or inside InvokeAsync).</summary>
    private void SelectQueue(ReadOnlyJobQueue queue)
    {
        _selectedQueue = queue as ReadOnlyClusterQueue;
        _submissionScriptCursorPosition = -1;

        _subscriptions.UnsubscribeAndClear();

        if (_selectedQueue != null)
            _subscriptions.Add(DataManager.QueueUpdated.Add(GroupName.Queue(_selectedQueue.Id), HandleQueueUpdated));

        StateHasChanged();
    }

    public void Dispose()
    {
        _listSubscriptions.UnsubscribeAndClear();
        _subscriptions.UnsubscribeAndClear();
    }

    #region Queue events

    private Task HandleQueueListChanged(GroupEventArgs<ReadOnlyJobQueue> args) =>
        InvokeAsync(StateHasChanged);

    private Task HandleQueueUpdated(GroupEventArgs<ReadOnlyJobQueue> args) =>
        InvokeAsync(StateHasChanged);

    private Task HandleQueueDeleted(GroupEventArgs<ReadOnlyJobQueue> args) =>
        InvokeAsync(() =>
        {
            if (_selectedQueue != null && _selectedQueue.Id == args.Object.Id)
                SelectQueue(null);
            else
                StateHasChanged();
        });

    #endregion
    
    #region Queue operations

    private async Task AddNewQueue()
    {
        var template = new ClusterQueue
        {
            Alias = "New Queue"
        };

        var newQueue = await DataManager.CreateClusterQueue(template);
        SelectQueue(newQueue);
    }

    private async Task CopySelectedQueue()
    {
        if (_selectedQueue == null)
            return;

        var template = new ClusterQueue();
        template.ReadFromJson(_selectedQueue.ToJson());
        template.Alias += " Copy";

        try
        {
            var newQueue = await DataManager.CreateClusterQueue(template);
            SelectQueue(newQueue);
        }
        catch (InvalidOperationException exc)
        {
            _ = ToastService.ShowErrorToastAsync(exc.Message);
        }
    }

    private async Task DeleteQueue(ReadOnlyJobQueue queue)
    {
        try
        {
            await DataManager.DeleteQueue(queue);
        }
        catch (InvalidOperationException exc)
        {
            _ = ToastService.ShowErrorToastAsync(exc.Message);
            return;
        }

        if (queue.Id == _selectedQueue?.Id)
            SelectQueue(null);
    }

    private async Task MoveQueueUp()
    {
        if (!CanMoveQueueUp)
            return;

        var index = DataManager.ClusterQueues.IndexOf(_selectedQueue);

        if (index > 0)
            await DataManager.MoveQueueToPosition(_selectedQueue, index - 1);
    }

    private async Task MoveQueueDown()
    {
        if (!CanMoveQueueDown)
            return;

        var index = DataManager.ClusterQueues.IndexOf(_selectedQueue);

        if (index < DataManager.ClusterQueues.Count - 1)
            await DataManager.MoveQueueToPosition(_selectedQueue, index + 1);
    }
    
    #endregion
    
    #region Template variables

    private IEnumerable<string> GetSubmissionScriptVariables()
    {
        var variables = new List<string>
        {
            "command",
            "username",
            "job_id",
            "attempt_id",
            "n_processes",
            "n_cores",
            "memory_gb",
            "n_gpus",
            "gpu_memory_gb",
            "run_directory",
            "std_out",
            "std_err"
        };

        if (_selectedQueue?.CustomVariables != null)
            variables.AddRange(_selectedQueue.CustomVariables.Keys);

        return variables;
    }

    private IEnumerable<string> GetCommandTemplateVariables()
    {
        return ["command"];
    }

    private IEnumerable<string> GetSubmitJobTemplateVariables()
    {
        return ["script_path_abs", "username"];
    }

    private IEnumerable<string> GetStatusJobTemplateVariables()
    {
        return ["job_id"];
    }

    private IEnumerable<string> GetAbortJobTemplateVariables()
    {
        return ["job_id"];
    }

    private IEnumerable<string> GetCancelManyJobsTemplateVariables()
    {
        return ["job_ids"];
    }

    private async Task InsertSubmissionScriptVariable(string variable)
    {
        if (_selectedQueue == null)
            return;
        
        bool shouldUpdateCursor = _submissionScriptCursorPosition >= 0;
        string insertText = $"{{{{ {variable} }}}}";
        
        await DataManager.UpdateQueue(_selectedQueue, queue =>
        {
            var clusterQueue = queue as ClusterQueue;
            if (_submissionScriptCursorPosition < 0)
                clusterQueue.SubmissionScriptTemplate += insertText;
            else
            {
                int position = Math.Min(_submissionScriptCursorPosition, clusterQueue.SubmissionScriptTemplate.Length);
                clusterQueue.SubmissionScriptTemplate = clusterQueue.SubmissionScriptTemplate.Insert(position, insertText);
            }
        });

        if (shouldUpdateCursor)
            await _submissionScriptEditor.SetCursorPositionAsync(_submissionScriptCursorPosition + insertText.Length);
    }

    private async Task InsertSubmissionScriptModule(string module)
    {
        if (_selectedQueue == null)
            return;
        
        bool shouldUpdateCursor = _submissionScriptCursorPosition >= 0;
        string insertText = $"{{{{ {module} }}}}\n\n{{{{ /{module} }}}}";
        
        await DataManager.UpdateQueue(_selectedQueue, queue =>
        {
            var clusterQueue = queue as ClusterQueue;
            if (_submissionScriptCursorPosition < 0)
                clusterQueue.SubmissionScriptTemplate += insertText;
            else
            {
                int position = Math.Min(_submissionScriptCursorPosition, clusterQueue.SubmissionScriptTemplate.Length);
                clusterQueue.SubmissionScriptTemplate = clusterQueue.SubmissionScriptTemplate.Insert(position, insertText);
            }
        });

        if (shouldUpdateCursor)
            await _submissionScriptEditor.SetCursorPositionAsync(_submissionScriptCursorPosition + insertText.Length);
    }

    private async Task InsertSettingVariable(string variable, string propertyName)
    {
        if (_selectedQueue == null)
            return;
        
        await DataManager.UpdateQueue(_selectedQueue, queue =>
        {
            var clusterQueue = queue as ClusterQueue;
            PropertyInfo prop = clusterQueue.GetType().GetProperty(propertyName);
            prop.SetValue(clusterQueue, prop.GetValue(clusterQueue) + $"{{{{ {variable} }}}}");
        });
    }
    
    
    private bool ScriptContains(string variable)
    {
        if (_selectedQueue == null)
            return false;
        
        var regex = new Regex($@"\{{\{{\s*{variable}\s*\}}\}}");
        return regex.IsMatch(_selectedQueue.SubmissionScriptTemplate);
    }
    
    #endregion
    
    #region Custom variables

    private async Task AddCustomVariable()
    {
        if (_selectedQueue == null)
            return;

        var newName = "new_variable";
        var suffix = 2;
        while (_selectedQueue.CustomVariables.ContainsKey(newName))
            newName = $"new_variable_{suffix++}";

        await DataManager.UpdateQueue(_selectedQueue, queue =>
        {
            var clusterQueue = queue as ClusterQueue;
            clusterQueue.CustomVariables.Add(newName, ("Description", "Default value"));
        });
    }

    private async Task DeleteCustomVariable(string key)
    {
        if (_selectedQueue == null)
            return;

        await DataManager.UpdateQueue(_selectedQueue, queue =>
        {
            var clusterQueue = queue as ClusterQueue;
            clusterQueue.CustomVariables.Remove(key);
        });
    }
    
    #endregion
    
    #region Field events

    private async Task HandleSubmissionScriptTemplateChanged(string? value)
    {
        await DataManager.UpdateQueue(_selectedQueue, queue =>
        {
            var clusterQueue = queue as ClusterQueue;
            clusterQueue.SubmissionScriptTemplate = value;
        });
    }
    
    private async Task HandleSubmissionScriptCursorPositionChanged((int start, int end) position)
    {
        _submissionScriptCursorPosition = position.start;
    }

    private async Task HandleFieldChanged(object value, string propertyName)
    {
        if (_selectedQueue == null)
            return;

        // A refused change — a second managed queue, or new totals while jobs are running — has to
        // be shown, and the control put back to the stored value rather than left showing the edit.
        try
        {
            await DataManager.UpdateQueue(_selectedQueue, queue =>
            {
                var clusterQueue = queue as ClusterQueue;
                PropertyInfo prop = clusterQueue.GetType().GetProperty(propertyName);
                prop.SetValue(clusterQueue, value);
            });
        }
        catch (InvalidOperationException exc)
        {
            _ = ToastService.ShowErrorToastAsync(exc.Message);
            await InvokeAsync(StateHasChanged);
        }
    }
    
    private async Task HandleCustomVariableKeyChanged(string oldValue, string newValue)
    {
        await DataManager.UpdateQueue(_selectedQueue, queue =>
        {
            var clusterQueue = queue as ClusterQueue;
            
            var variable = clusterQueue.CustomVariables[oldValue];
            clusterQueue.CustomVariables.Remove(oldValue);
            clusterQueue.CustomVariables[newValue] = variable;
        });
    }
    
    private async Task HandleCustomVariableDescriptionChanged(string key, string value)
    {
        await DataManager.UpdateQueue(_selectedQueue, queue =>
        {
            var clusterQueue = queue as ClusterQueue;
            clusterQueue.CustomVariables[key] = (value, clusterQueue.CustomVariables[key].defaultValue);
        });
    }
    
    private async Task HandleCustomVariableValueChanged(string key, string value)
    {
        await DataManager.UpdateQueue(_selectedQueue, queue =>
        {
            var clusterQueue = queue as ClusterQueue;
            clusterQueue.CustomVariables[key] = (clusterQueue.CustomVariables[key].description, value);
        });
    }
    
    #endregion
}

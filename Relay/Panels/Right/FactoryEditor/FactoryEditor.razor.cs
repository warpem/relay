using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Relay.Panels.Right.FactoryEditor;

/// <summary>
/// A component that provides a form for editing factory instance parameters.
/// </summary>
/// <remarks>
/// The FactoryEditor component is the main interface for configuring exposed properties
/// of a factory instance's sub-jobs, organized into collapsible groups by sub-job.
///
/// Features include:
/// - Exposed properties grouped by sub-job in accordion items
/// - Properties disabled for sub-jobs not in Building status
/// - Exposed input port display with connection info
/// - Queue wizard trigger for submitting the factory instance
/// </remarks>
public partial class FactoryEditor : ComponentBase, IDisposable
{
    /// <summary>
    /// The factory instance currently being edited.
    /// </summary>
    private ReadOnlyFactoryInstance _instance;

    /// <summary>
    /// Whether the queue wizard is currently shown.
    /// </summary>
    private bool _showQueueWizard;

    /// <summary>
    /// Gets or sets the factory editor service that manages the current instance being edited.
    /// </summary>
    [Inject]
    private FactoryEditorService Editor { get; set; }

    /// <summary>
    /// Gets or sets the data manager service for performing operations on sub-jobs.
    /// </summary>
    [Inject]
    private DataManager DataManager { get; set; }

    /// <summary>
    /// Gets or sets the current session context.
    /// </summary>
    [Inject]
    private RelaySession Session { get; set; }

    /// <summary>
    /// Gets or sets the toast service for showing notifications.
    /// </summary>
    [Inject]
    private IToastService ToastService { get; set; }

    /// <summary>
    /// Initializes the component and sets up event subscriptions.
    /// </summary>
    protected override void OnInitialized()
    {
        Editor.OnInstanceChanged += HandleInstanceChanged;
        Editor.OnInstanceUpdated += HandleInstanceUpdated;
        _instance = Editor.CurrentInstance;
    }

    /// <summary>
    /// Handles instance change events from the editor service.
    /// </summary>
    /// <param name="instance">The new factory instance being edited</param>
    private async Task HandleInstanceChanged(ReadOnlyFactoryInstance instance)
    {
        _instance = instance;
        _showQueueWizard = false;
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Handles instance update events from the editor service.
    /// </summary>
    /// <param name="instance">The updated factory instance</param>
    private async Task HandleInstanceUpdated(ReadOnlyFactoryInstance instance)
    {
        _instance = instance;
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Groups exposed properties by their sub-job blueprint ID.
    /// </summary>
    private IEnumerable<IGrouping<int, ExposedProperty>> GroupedProperties =>
        _instance?.Definition?.ExposedProperties
            .GroupBy(p => p.SubJobId) ?? Enumerable.Empty<IGrouping<int, ExposedProperty>>();

    /// <summary>
    /// Maps a blueprint sub-job ID to the corresponding real sub-job in the instance.
    /// Blueprint IDs are 1-based and correspond to indices in the SubJobIds list.
    /// </summary>
    /// <param name="blueprintId">The blueprint-local sub-job ID</param>
    /// <returns>The resolved sub-job, or null if not found</returns>
    private ReadOnlyJob GetSubJob(int blueprintId)
    {
        if (_instance == null) return null;
        int index = blueprintId - 1; // Blueprint IDs are 1-based
        if (index < 0 || index >= _instance.SubJobIds.Count) return null;
        var subJobId = _instance.SubJobIds[index];
        return _instance.SubJobs.FirstOrDefault(j => j.Id == subJobId);
    }

    /// <summary>
    /// Resolves a PropertyInfo for a given property name on a sub-job, using the static
    /// TypeParameters registry.
    /// </summary>
    /// <param name="subJob">The sub-job to look up the property on</param>
    /// <param name="propertyName">The property name from ExposedProperty</param>
    /// <returns>The PropertyInfo, or null if not found</returns>
    private PropertyInfo GetPropertyInfo(ReadOnlyJob subJob, string propertyName)
    {
        if (subJob == null || string.IsNullOrEmpty(propertyName)) return null;

        var jobType = subJob.GetOriginalType();
        if (!Job.TypeParameters.TryGetValue(jobType, out var parameters)) return null;

        return parameters.FirstOrDefault(p => p.Name == propertyName);
    }

    /// <summary>
    /// Updates the instance alias when changed in the UI.
    /// </summary>
    private async Task HandleAliasChanged(string alias)
    {
        if (_instance == null || alias == _instance.Alias) return;

        try
        {
            await DataManager.UpdateFactoryInstance(Session.User, _instance,
                fi => fi.Alias = alias);
        }
        catch (Exception exc)
        {
            ToastService.ShowError($"Couldn't update name: {exc.Message}");
        }
    }

    /// <summary>
    /// Handles parameter value changes from UiFieldView for a specific sub-job.
    /// </summary>
    /// <param name="subJob">The sub-job whose parameter changed</param>
    /// <param name="args">A tuple containing the property and its new value</param>
    private async Task HandleParameterChanged(ReadOnlyJob subJob, (PropertyInfo prop, object value) args)
    {
        if (subJob == null || subJob.Status != JobStatus.Building) return;

        try
        {
            await DataManager.UpdateJob(Session.User, subJob,
                originalJob =>
                {
                    args.prop.SetValue(originalJob, args.value);
                });
        }
        catch (Exception exc)
        {
            ToastService.ShowError($"Couldn't update parameter: {exc.Message}");
        }
    }

    /// <summary>
    /// Removes a connection from an input port.
    /// </summary>
    /// <param name="edge">The edge to remove</param>
    private async Task HandlePortEdgeRemoved(ReadOnlyEdge edge)
    {
        if (edge == null)
            return;

        try
        {
            await DataManager.DeleteEdge(edge);
        }
        catch (Exception exc)
        {
            ToastService.ShowError($"Couldn't remove edge: {exc.Message}");
        }
    }

    /// <summary>
    /// Checks if the factory instance can be queued (has at least one sub-job in Building status).
    /// </summary>
    private bool CanQueue => _instance?.SubJobs.Any(j => j.Status == JobStatus.Building) == true;

    /// <summary>
    /// Whether all sub-jobs in the factory instance are local-only jobs.
    /// </summary>
    private bool IsAllLocal => _instance?.SubJobs
        .Where(j => j.Status == JobStatus.Building)
        .All(j => typeof(ILocalJob).IsAssignableFrom(j.GetOriginalType())) == true;

    /// <summary>
    /// Runs all sub-jobs locally without showing the queue wizard.
    /// </summary>
    private async Task HandleRunLocally()
    {
        if (_instance == null) return;

        try
        {
            var queueAssignments = _instance.SubJobIds.ToDictionary(id => id, _ => -1);
            await DataManager.RunFactoryInstance(Session.User, _instance, queueAssignments);
            await Editor.SetInstance(null);
        }
        catch (Exception exc)
        {
            ToastService.ShowError($"Couldn't run {_instance.QualifiedName}: {exc.Message}");
        }
    }

    /// <summary>
    /// Gets a user-friendly text representation of a port's connection requirements.
    /// </summary>
    private string GetPortRequirementText(ReadOnlyPortIn port)
    {
        if (port.MaxItems == int.MaxValue)
            return $"{port.MinItems}+";
        if (port.MinItems == port.MaxItems)
            return port.MinItems.ToString();
        return $"{port.MinItems}–{port.MaxItems}";
    }

    /// <summary>
    /// Cleans up event subscriptions when the component is disposed.
    /// </summary>
    public void Dispose()
    {
        Editor.OnInstanceChanged -= HandleInstanceChanged;
        Editor.OnInstanceUpdated -= HandleInstanceUpdated;
    }
}

using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel.ReadOnly;
using Refund.Jobs._3D.Class3D;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;

namespace Refund.Jobs._3D.InitialReference3D;

/// <summary>
/// Component for the expanded view of a Class3D job.
/// Displays class volumes, statistics, and provides selection functionality
/// for exporting selected classes to a new job.
/// </summary>
/// <remarks>
/// This component is referenced directly in the Class3D job type definition through the
/// ExpandedViewType property:
/// 
/// ```csharp
/// public override Type ExpandedViewType => typeof(Class3DExpandedView);
/// ```
/// 
/// When a Class3D job is selected in the UI, the ExpandedJobViewService uses this type
/// information to instantiate the appropriate expanded view component. This architecture
/// allows for specialized visualization components for each job type while maintaining
/// a consistent interface through the ExpandedJobViewService.
/// 
/// The component integrates with multiple services:
/// - ExpandedJobViewService for state management and iteration control
/// - DataManager for creating downstream selection jobs
/// - RelaySession for user context and view access
/// 
/// It plays a central role in the heterogeneity analysis workflow by enabling visual selection
/// of promising 3D classes for further refinement.
/// </remarks>
public partial class InitialReferenceExpandedView
{
    /// <summary>
    /// Service for managing expanded job view state
    /// </summary>
    [Inject] private ExpandedJobViewService _expandedViewService { get; set; }
    
    /// <summary>
    /// Service for displaying toast notifications
    /// </summary>
    [Inject] private IToastService _toastService { get; set; }
    
    /// <summary>
    /// Central data management service
    /// </summary>
    [Inject] private DataManager _dataManager { get; set; }
    
    /// <summary>
    /// Current session information
    /// </summary>
    [Inject] private RelaySession _session { get; set; }
    
    /// <summary>
    /// The Class3D job currently being viewed
    /// </summary>
    private ReadOnlyInitialReference _job;
    
    /// <summary>
    /// Whether the mask visualization is checked/visible
    /// </summary>
    private bool _isMaskChecked;
    
    /// <summary>
    /// Array of models containing statistics for each class
    /// </summary>
    private Class3DModel[] _class3DModels;
    
    /// <summary>
    /// Set of currently selected class indices (0-based)
    /// </summary>
    private readonly HashSet<int> _selectedClasses = new();

    /// <summary>
    /// Initializes the component and sets up event handlers
    /// </summary>
    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        
        // Subscribe to events from the expanded view service
        _expandedViewService.OnJobChanged += HandleJobChanged;
        _expandedViewService.OnJobUpdated += HandleJobUpdated;
        _expandedViewService.OnIterationChanged += HandleIterationChanged;
        
        // Load initial job data
        await HandleJobChanged(_expandedViewService.CurrentJob);
    }

    /// <summary>
    /// Handles changes to the currently displayed job
    /// </summary>
    /// <param name="job">The new job being displayed</param>
    private async Task HandleJobChanged(ReadOnlyJob job)
    {
        // Check if this is a Class3D job
        if (job is ReadOnlyInitialReference initialReference)
        {
            _job = initialReference;
            _selectedClasses.Clear();
            UpdateData();
        }
        else
        {
            _job = null;
        }
        
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Handles updates to the current job's data
    /// </summary>
    private async Task HandleJobUpdated()
    {
        UpdateData();
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Handles changes to the displayed iteration
    /// </summary>
    /// <param name="iteration">The new iteration number</param>
    private async Task HandleIterationChanged(int iteration)
    {
        _selectedClasses.Clear();
        UpdateData();
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Updates the class statistics data from the current iteration's results
    /// </summary>
    private void UpdateData()
    {
        if (_job == null)
            return;

        try
        {
            // Load class statistics from the JSON file if it exists
            if (File.Exists(_job.VisClassStats(_expandedViewService.CurrentVisIteration)))
            {
                _class3DModels = JsonSerializer.Deserialize<Class3DModel[]>(
                    File.ReadAllText(_job.VisClassStats(_expandedViewService.CurrentVisIteration)));
            }
            else
            {
                _class3DModels = null;
            }
        }
        catch { }
    }

    /// <summary>
    /// Toggles selection state of a class
    /// </summary>
    /// <param name="selectedClass">The 0-based index of the class to toggle</param>
    private void SetSelected(int selectedClass)
    {
        // Toggle the class in the selected set
        if (!_selectedClasses.Add(selectedClass))
        {
            _selectedClasses.Remove(selectedClass);
        }
        
        StateHasChanged();
    }

    /// <summary>
    /// Creates a new Class3DSelect job using the currently selected classes
    /// </summary>
    /// <remarks>
    /// This method implements the critical workflow step of creating a Class3DSelect job
    /// from user-selected classes. It demonstrates the standard pattern for programmatic job creation:
    /// 
    /// 1. Creates a job instance with appropriate parameters (converting 0-based UI indices to 1-based RELION class numbers)
    /// 2. Uses DataManager.CreateJob to register the job in the data model
    /// 3. Creates edges connecting input ports from the parent job to the new selection job
    /// 4. Queues the job for local execution
    /// 5. Updates the view layout to incorporate the new job
    /// 
    /// This programmatic job creation pattern is a key architectural feature that enables
    /// visual and interactive workflow building rather than requiring manual job configuration.
    /// The same pattern is used in other selection components throughout the application.
    /// </remarks>
    private async Task ExportClasses()
    {
        try
        {
            if (_job == null)
                return;

            // Create a Class3DSelect job with the selected classes
            var select3d = new Class3DSelect.Class3DSelect
            {
                // Convert 0-based UI indices to 1-based RELION class numbers
                SelectedClasses = _selectedClasses.Select(i => i + 1).Order().ToArray(),
                SelectedIteration = _expandedViewService.CurrentVisIteration
            };

            // Get the current view
            var view = _session.View;
            if (view == null)
                throw new Exception("Current view not found");

            // Create the selection job
            var createdJob = await _dataManager.CreateJob(_session.User, view, select3d.TypeGuid, select3d);
            if (createdJob == null)
                throw new Exception("Failed to create selection job");

            // Connect the input ports from this job to the new selection job
            await _dataManager.CreateEdge(_job.Space, 
                                          _job.PortsOut[InitialReference.PortOutMaps], 
                                          createdJob.PortsIn[Class3DSelect.Class3DSelect.PortInMaps]);
            await _dataManager.CreateEdge(_job.Space, 
                                          _job.PortsOut[InitialReference.PortOutParticles], 
                                          createdJob.PortsIn[Class3DSelect.Class3DSelect.PortInParticles]);

            // Queue the job for execution
            await _dataManager.QueueLocalJob(_session.User, createdJob);

            // Show success notification and clear selections
            _toastService.ShowSuccess($"Created selection from {_job.QualifiedName}");
            _selectedClasses.Clear();
            StateHasChanged();
        }
        catch(Exception ex)
        {
            _toastService.ShowError($"Failed to create selection: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Unsubscribes from events when the component is disposed
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _expandedViewService.OnJobChanged -= HandleJobChanged;
        _expandedViewService.OnJobUpdated -= HandleJobUpdated;
        _expandedViewService.OnIterationChanged -= HandleIterationChanged;
    }
}
using Refund.Jobs.Refinement.Classes3D.Class3D;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel.ReadOnly;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;

namespace Refund.Jobs.Refinement.Classes3D.BeamClass3D;
public partial class BeamClass3DExpandedView
{
    [Inject] private ExpandedJobViewService _expandedViewService { get; set; }
    [Inject] private INotificationService _toastService { get; set; }
    [Inject] private DataManager _dataManager { get; set; }
    [Inject] private RelaySession _session { get; set; }
    private ReadOnlyBeamClass3D _job;
    private Class3DModel[] _class3DModels;
    private readonly HashSet<int> _selectedClasses = new();
    protected override async Task InitializeViewAsync()
    {
        // Subscribe to events from the expanded view service
        _expandedViewService.OnJobChanged += HandleJobChanged;
        _expandedViewService.OnJobUpdated += HandleJobUpdated;
        _expandedViewService.OnIterationChanged += HandleIterationChanged;

        // Load initial job data
        await HandleJobChanged(_expandedViewService.CurrentJob);
    }
    private async Task HandleJobChanged(ReadOnlyJob job)
    {
        // Check if this is a Class3D job
        if (job is ReadOnlyBeamClass3D class3D)
        {
            _job = class3D;
            _selectedClasses.Clear();
            UpdateData();
        }
        else
        {
            _job = null;
        }

        await InvokeAsync(StateHasChanged);
    }
    private async Task HandleJobUpdated()
    {
        UpdateData();
        await InvokeAsync(StateHasChanged);
    }
    private async Task HandleIterationChanged(int iteration)
    {
        _selectedClasses.Clear();
        UpdateData();
        await InvokeAsync(StateHasChanged);
    }
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
        catch (IOException) { _class3DModels = null; }
        catch (JsonException) { _class3DModels = null; }
    }
    private void SetSelected(int selectedClass)
    {
        // Toggle the class in the selected set
        if (!_selectedClasses.Add(selectedClass))
        {
            _selectedClasses.Remove(selectedClass);
        }

        StateHasChanged();
    }
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
                                          _job.PortsOut["Reference"],
                                          createdJob.PortsIn[Class3DSelect.Class3DSelect.PortInMaps]);
            await _dataManager.CreateEdge(_job.Space,
                                          _job.PortsOut["Particles"],
                                          createdJob.PortsIn[Class3DSelect.Class3DSelect.PortInParticles]);

            if (_job.IsTiltSeries)
                await _dataManager.CreateEdge(_job.Space, _job.PortsOut["TiltSeries"], createdJob.PortsIn["TiltSeries"]);

            // Queue the job for execution
            await _dataManager.QueueLocalJob(_session.User, createdJob);

            // Show success notification and clear selections
            _ = _toastService.ShowSuccessToastAsync($"Created selection from {_job.QualifiedName}");
            _selectedClasses.Clear();
            StateHasChanged();
        }
        catch(Exception ex)
        {
            _ = _toastService.ShowErrorToastAsync($"Failed to create selection: {ex.Message}");
        }
    }
    public async ValueTask DisposeAsync()
    {
        _expandedViewService.OnJobChanged -= HandleJobChanged;
        _expandedViewService.OnJobUpdated -= HandleJobUpdated;
        _expandedViewService.OnIterationChanged -= HandleIterationChanged;
    }
}

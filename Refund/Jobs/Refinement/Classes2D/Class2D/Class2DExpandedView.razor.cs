using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.Components.Class2DSelection;
using Refund.Components.Jobs.ExpandedView;
using Refund.DataModel.ReadOnly;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;

namespace Refund.Jobs.Refinement.Classes2D.Class2D;

public partial class Class2DExpandedView : IDisposable
{
    [Inject] private ExpandedJobViewService _expandedViewService { get; set; }
    [Inject] private IToastService _toastService { get; set; }
    [Inject] private DataManager _dataManager { get; set; }
    [Inject] private RelaySession _session { get; set; }
    
    private ReadOnlyClass2D _job;
    private Class2DSelection _class2DSelectionComponent;
    private int _nClassesSelected;
    private int[] _classNumbers;
    private int[] _classIdx;
    private float[] _classResolutions;
    private float[] _classSizes;

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        
        _expandedViewService.OnJobChanged += HandleJobChanged;
        _expandedViewService.OnJobUpdated += HandleJobUpdated;
        _expandedViewService.OnIterationChanged += HandleIterationChanged;
        await HandleJobChanged(_expandedViewService.CurrentJob);
    }
    
    private async Task HandleJobChanged(ReadOnlyJob job)
    {
        if (job is ReadOnlyClass2D class2D)
        {
            _job = class2D;
            _classNumbers = Enumerable.Range(1, _job.NClasses).ToArray();
            _nClassesSelected = 0;
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
        UpdateData();
        await InvokeAsync(StateHasChanged);
    }

    private void UpdateData()
    {
        if (_job == null)
            return;

        try
        {
            if (File.Exists(_job.VisClassStats(_expandedViewService.CurrentVisIteration)))
            {
                var class2DModels = JsonSerializer.Deserialize<Class2DModel[]>(File.ReadAllText(_job.VisClassStats(_expandedViewService.CurrentVisIteration)));

                _classIdx = class2DModels.Select(m => m.Id).ToArray();

                _classResolutions = class2DModels.All(m => m.Resolution != null) ?
                                        class2DModels.Select(m => m.Resolution.Value).ToArray() :
                                        null;

                _classSizes = class2DModels.All(m => m.Distribution != null) ?
                                  class2DModels.Select(m => m.Distribution.Value).ToArray() :
                                  null;
            }
            else
            {
                _classIdx = null;
                _classResolutions = null;
                _classSizes = null;
            }
        }
        catch { }
    }

    private async Task SaveSelectionState()
    {
        try
        {
            if (_job == null || _class2DSelectionComponent == null)
                return;

            var selectedClasses = _class2DSelectionComponent.selectedClasses;
            var unselectedClasses = _classNumbers.Except(selectedClasses).Order().ToArray();

            var class2DSelection = new Class2DSelect.Class2DSelect
            {
                SelectedClasses = selectedClasses.Order().ToArray(),
                UnselectedClasses = unselectedClasses.Any() ? unselectedClasses : []
            };

            var view = _session.View;
            if (view == null)
                throw new Exception("Current view not found");

            var createdJob = await _dataManager.CreateJob(_session.User, view, class2DSelection.TypeGuid, class2DSelection);
            if (createdJob == null)
                throw new Exception("Failed to create selection job");

            await _dataManager.CreateEdge(_job.Space, _job.PortsOut["Particles"], createdJob.PortsIn["Particles"]);
            await _dataManager.CreateEdge(_job.Space, _job.PortsOut["Templates"], createdJob.PortsIn["Templates"]);

            await _dataManager.QueueLocalJob(_session.User, createdJob);

            _toastService.ShowSuccess($"Created selection from {_job.QualifiedName}");
        }
        catch(Exception ex)
        {
            _toastService.ShowError($"Failed to create selection: {ex.Message}");
        }
    }

    private void ClearAll()
    {
        if (_class2DSelectionComponent == null) 
            return;
            
        _class2DSelectionComponent.selectedClasses.Clear();
        _nClassesSelected = 0;
        StateHasChanged();
    }

    private void SelectAll()
    {
        if (_class2DSelectionComponent == null || _classNumbers == null) 
            return;
            
        foreach(var classNumber in _classNumbers)
        {
            _class2DSelectionComponent.selectedClasses.Add(classNumber);
        }

        _nClassesSelected = _classNumbers.Length;
        StateHasChanged();
    }

    private void SelectionChanged()
    {
        if (_class2DSelectionComponent == null)
            return;
            
        _nClassesSelected = _class2DSelectionComponent.selectedClasses.Count;
        StateHasChanged();
    }
    
    public void Dispose()
    {
        _expandedViewService.OnJobChanged -= HandleJobChanged;
        _expandedViewService.OnJobUpdated -= HandleJobUpdated;
        _expandedViewService.OnIterationChanged -= HandleIterationChanged;
    }
}
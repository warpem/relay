using Refund.Jobs.Refinement.Classes2D.BeamClass2D;
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
    [Inject] private INotificationService _toastService { get; set; }
    [Inject] private DataManager _dataManager { get; set; }
    [Inject] private RelaySession _session { get; set; }
    
    private ReadOnlyJob _job;
    private ReadOnlyBeamClass2D Beam => _job as ReadOnlyBeamClass2D;
    private int Iteration => _expandedViewService.CurrentVisIteration;
    private bool CanExport => Beam == null || Beam.CanExport(Iteration);
    private int NClasses => _classNumbers?.Length ?? 0;
    private string ClassAtlas => Beam?.VisClassAtlas(Iteration) ?? ((ReadOnlyClass2D)_job).VisClassAtlas(Iteration);
    private string ClassStats => Beam?.VisClassStats(Iteration) ?? ((ReadOnlyClass2D)_job).VisClassStats(Iteration);
    private int _pcaCount;
    private int _embeddingCount;
    private string[] PcaLabels => new[] { "Mean" }.Concat(Enumerable.Range(1, _pcaCount).Select(i => $"PC {i}")).ToArray();
    private int _displayedIteration = -1;
    private BeamSubclassGroup[] _subclassGroups = [];
    private readonly Dictionary<int, Class2DSelection> _subclassSelections = new();
    private int _selectedSubclassCount;
    private bool ShowSubclasses => Beam != null && Beam.CanExport(Iteration) && _subclassGroups.Length > 0;

    private Class2DSelection _class2DSelectionComponent;
    private int _nClassesSelected;
    private int[] _classNumbers;
    private int[] _classIdx;
    private float[] _classResolutions;
    private float[] _classSizes;

    protected override async Task InitializeViewAsync()
    {
        _expandedViewService.OnJobChanged += HandleJobChanged;
        _expandedViewService.OnJobUpdated += HandleJobUpdated;
        _expandedViewService.OnIterationChanged += HandleIterationChanged;
        await HandleJobChanged(_expandedViewService.CurrentJob);
    }
    
    private async Task HandleJobChanged(ReadOnlyJob job)
    {
        if (job is ReadOnlyClass2D or ReadOnlyBeamClass2D)
        {
            _job = job;
            _subclassGroups = [];
            _subclassSelections.Clear();
            _selectedSubclassCount = 0;
            _classNumbers = [];
            _nClassesSelected = 0;
            UpdateData();
        }
        else
        {
            _job = null;
        }
        
        await InvokeAsync(StateHasChanged);
    }

    private Task HandleJobUpdated() => InvokeAsync(() =>
    {
        UpdateData();
        StateHasChanged();
    });

    private Task HandleIterationChanged(int iteration) => HandleJobUpdated();

    private void UpdateData()
    {
        if (_job == null)
            return;

        _subclassGroups = [];
        if (Beam != null && File.Exists(Beam.VisSubclassGroups))
        {
            try { _subclassGroups = JsonSerializer.Deserialize<BeamSubclassGroup[]>(File.ReadAllText(Beam.VisSubclassGroups)) ?? []; }
            catch (JsonException) { }
        }
        _pcaCount = _embeddingCount = 0;
        _classIdx = null; _classResolutions = _classSizes = null;
        _classNumbers = _job is ReadOnlyClass2D relion ? Enumerable.Range(1, relion.NClasses).ToArray() : [];
        if (Beam != null && _displayedIteration != Iteration)
        {
            _class2DSelectionComponent?.selectedClasses.Clear();
            _subclassSelections.Clear();
            _selectedSubclassCount = 0;
            _nClassesSelected = 0;
            _displayedIteration = Iteration;
        }
        try
        {
            if (Beam != null && File.Exists(Beam.VisPcaInfo(Iteration)))
            {
                using var info = JsonDocument.Parse(File.ReadAllText(Beam.VisPcaInfo(Iteration)));
                _pcaCount = info.RootElement.GetProperty("PrincipalComponentCount").GetInt32();
                _embeddingCount = info.RootElement.GetProperty("EmbeddingParticleCount").GetInt32();
            }
            if (File.Exists(ClassStats))
            {
                var class2DModels = JsonSerializer.Deserialize<Class2DModel[]>(File.ReadAllText(ClassStats));

                _classIdx = class2DModels.Select(m => m.Id).ToArray();
                _classNumbers = Enumerable.Range(1, class2DModels.Length).ToArray();

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
            if (_job == null || _class2DSelectionComponent == null || !CanExport)
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

            _ = _toastService.ShowSuccessToastAsync($"Created selection from {_job.QualifiedName}");
        }
        catch(Exception ex)
        {
            _ = _toastService.ShowErrorToastAsync($"Failed to create selection: {ex.Message}");
        }
    }

    private void SubclassSelectionChanged()
    {
        _selectedSubclassCount = _subclassSelections.Values.Sum(c => c.selectedClasses.Count);
        StateHasChanged();
    }

    private void ClearSubclasses()
    {
        foreach (var component in _subclassSelections.Values) component.selectedClasses.Clear();
        _selectedSubclassCount = 0;
    }

    private void SelectAllSubclasses()
    {
        foreach (var group in _subclassGroups)
            if (_subclassSelections.TryGetValue(group.ParentId, out var component))
                foreach (int child in Enumerable.Range(1, group.Classes.Length)) component.selectedClasses.Add(child);
        SubclassSelectionChanged();
    }

    private async Task SaveSubclassSelection()
    {
        if (!ShowSubclasses || !Beam.SubclassSelectionAvailable) return;
        try
        {
            var selected = _subclassGroups.Where(g => _subclassSelections.ContainsKey(g.ParentId))
                .SelectMany(g => _subclassSelections[g.ParentId].selectedClasses.Select(c => g.FirstClass + c - 1)).Order().ToArray();
            if (selected.Length == 0) return;
            var all = _subclassGroups.SelectMany(g => Enumerable.Range(g.FirstClass, g.Classes.Length));
            var selection = new Class2DSelect.Class2DSelect
            {
                SelectedClasses = selected,
                UnselectedClasses = all.Except(selected).Order().ToArray()
            };
            var view = _session.View ?? throw new Exception("Current view not found");
            var created = await _dataManager.CreateJob(_session.User, view, selection.TypeGuid, selection)
                ?? throw new Exception("Failed to create selection job");
            await _dataManager.CreateEdge(_job.Space, _job.PortsOut["SubclassParticles"], created.PortsIn["Particles"]);
            await _dataManager.CreateEdge(_job.Space, _job.PortsOut["SubclassTemplates"], created.PortsIn["Templates"]);
            await _dataManager.QueueLocalJob(_session.User, created);
            _ = _toastService.ShowSuccessToastAsync($"Created subclass selection from {_job.QualifiedName}");
        }
        catch (Exception error) { _ = _toastService.ShowErrorToastAsync($"Failed to create subclass selection: {error.Message}"); }
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
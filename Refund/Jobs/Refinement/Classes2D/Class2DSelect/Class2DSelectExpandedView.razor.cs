using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Refund.Components.Jobs.ExpandedView;
using Refund.DataModel.ReadOnly;
using Refund.Jobs.Refinement.Classes2D.Class2D;
using Refund.Services;

namespace Refund.Jobs.Refinement.Classes2D.Class2DSelect;

public partial class Class2DSelectExpandedView : IDisposable
{
    [Inject]
    private ExpandedJobViewService _expandedViewService { get; set; }
    
    private ReadOnlyClass2DSelect _job;
    
    private int[] _selectedClassIdx = null;
    private float[] _selectedClassResolutions = null;
    private float[] _selectedClassSizes = null;
    
    private int[] _unselectedClassIdx = null;
    private float[] _unselectedClassResolutions = null;
    private float[] _unselectedClassSizes = null;

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        
        _expandedViewService.OnJobChanged += HandleJobChanged;
        _expandedViewService.OnJobUpdated += HandleJobUpdated;
        await HandleJobChanged(_expandedViewService.CurrentJob);
    }
    
    private async Task HandleJobChanged(ReadOnlyJob job)
    {
        if (job is ReadOnlyClass2DSelect class2DSelect)
        {
            _job = class2DSelect;
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

    private void UpdateData()
    {
        if (_job == null)
            return;
        
        try
        {
            if (File.Exists(_job.VisSelectedClassStats))
            {
                Class2DModel[] class2DModels = JsonSerializer.Deserialize<Class2DModel[]>(File.ReadAllText(_job.VisSelectedClassStats));
                _selectedClassIdx = class2DModels.Select(m => m.Id).ToArray();

                _selectedClassResolutions = class2DModels.All(m => m.Resolution != null) ? 
                                                class2DModels.Select(m => m.Resolution.Value).ToArray() : 
                                                null;

                _selectedClassSizes = class2DModels.All(m => m.Distribution != null) ? 
                                          class2DModels.Select(m => m.Distribution.Value).ToArray() : 
                                          null;
            }
            else
            {
                _selectedClassIdx = null;
                _selectedClassResolutions = null;
                _selectedClassSizes = null;
            }

            if (File.Exists(_job.VisUnselectedClassStats))
            {
                Class2DModel[] class2DModels = JsonSerializer.Deserialize<Class2DModel[]>(File.ReadAllText(_job.VisUnselectedClassStats));
                _unselectedClassIdx = class2DModels.Select(m => m.Id).ToArray();

                _unselectedClassResolutions = class2DModels.All(m => m.Resolution != null) ? 
                                                  class2DModels.Select(m => m.Resolution.Value).ToArray() : 
                                                  null;

                _unselectedClassSizes = class2DModels.All(m => m.Distribution != null) ? 
                                            class2DModels.Select(m => m.Distribution.Value).ToArray() : 
                                            null;
            }
            else
            {
                _unselectedClassIdx = null;
                _unselectedClassResolutions = null;
                _unselectedClassSizes = null;
            }
        }
        catch { }
    }
    
    public void Dispose()
    {
        _expandedViewService.OnJobChanged -= HandleJobChanged;
        _expandedViewService.OnJobUpdated -= HandleJobUpdated;
    }
}
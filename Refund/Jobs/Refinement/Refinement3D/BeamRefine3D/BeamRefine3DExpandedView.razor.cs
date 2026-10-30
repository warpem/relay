using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel.ReadOnly;
using Refund.Services;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Refund.Jobs.Refinement.Refinement3D.BeamRefine3D;

/// <summary>
/// Component for the expanded view of a BeamRefine3D job.
/// Displays the refined map visualizations including orthoslices,
/// FSC plots, and angular distribution.
/// </summary>
public partial class BeamRefine3DExpandedView
{
    /// <summary>
    /// Service for managing expanded job view state
    /// </summary>
    [Inject] private ExpandedJobViewService ExpandedViewService { get; set; }
    
    /// <summary>
    /// Service for displaying toast notifications
    /// </summary>
    [Inject] private INotificationService ToastService { get; set; }
    [Inject] private FileService FileService { get; set; }
    
    /// <summary>
    /// The BeamRefine3D job currently being viewed
    /// </summary>
    private ReadOnlyBeamRefine3D _job;

    private int Iteration => ExpandedViewService.CurrentVisIteration < 0
        ? _job.VisAvailableIteration : ExpandedViewService.CurrentVisIteration;
    private string CacheKey => _job.UpdateDate.Ticks.ToString();
    private Diagnostics _diagnostics;
    private bool _showCtf;
    private int _selectedOpticsGroup;
    private int _loadVersion;

    private sealed class Diagnostics
    {
        public Diagnostics() { }
        public bool History { get; set; }
        public CtfDiagnostics Ctf { get; set; }
    }
    private sealed class CtfDiagnostics
    {
        public CtfDiagnostics() { }
        public int Baseline { get; set; }
        [JsonPropertyName("matched_particles")] public int MatchedParticles { get; set; }
        [JsonPropertyName("group_count")] public int GroupCount { get; set; }
        public List<OpticsGroup> Groups { get; set; } = [];
    }
    private sealed class OpticsGroup
    {
        public OpticsGroup() { }
        public int Id { get; set; }
        public int Particles { get; set; }
    }

    private async Task RefreshDiagnostics()
    {
        int version = ++_loadVersion;
        Diagnostics diagnostics = null;
        string path = _job?.VisDiagnostics(Iteration);
        if (File.Exists(path))
        {
            try
            {
                diagnostics = JsonSerializer.Deserialize<Diagnostics>(await File.ReadAllTextAsync(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (IOException) { /* A concurrently cleared job has no figures. */ }
            catch (JsonException) { /* Older/incomplete diagnostic metadata remains optional. */ }
        }
        if (version != _loadVersion) return;
        _diagnostics = diagnostics;
        if (diagnostics?.Ctf == null) _showCtf = false;
        var groups = diagnostics?.Ctf?.Groups;
        if (groups?.Any(g => g.Id == _selectedOpticsGroup) != true)
            _selectedOpticsGroup = groups?.FirstOrDefault()?.Id ?? 0;
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Initializes the component and sets up event handlers
    /// </summary>
    protected override async Task InitializeViewAsync()
    {
        // Subscribe to events from the expanded view service
        ExpandedViewService.OnJobChanged += HandleJobChanged;
        ExpandedViewService.OnJobUpdated += HandleJobUpdated;
        ExpandedViewService.OnIterationChanged += HandleIterationChanged;
        
        // Load initial job data
        await HandleJobChanged(ExpandedViewService.CurrentJob);
    }

    /// <summary>
    /// Handles changes to the currently displayed job
    /// </summary>
    /// <param name="job">The new job being displayed</param>
    private async Task HandleJobChanged(ReadOnlyJob job)
    {
        // Check if this is a BeamRefine3D job
        if (job is ReadOnlyBeamRefine3D refine3D)
        {
            _job = refine3D;
        }
        else
        {
            _job = null;
        }
        
        await RefreshDiagnostics();
    }

    /// <summary>
    /// Handles updates to the current job's data
    /// </summary>
    private async Task HandleJobUpdated()
    {
        await RefreshDiagnostics();
    }

    /// <summary>
    /// Handles changes to the displayed iteration
    /// </summary>
    /// <param name="iteration">The new iteration number</param>
    private async Task HandleIterationChanged(int iteration)
    {
        await RefreshDiagnostics();
    }
    
    /// <summary>
    /// Unsubscribes from events when the component is disposed
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _loadVersion++;
        ExpandedViewService.OnJobChanged -= HandleJobChanged;
        ExpandedViewService.OnJobUpdated -= HandleJobUpdated;
        ExpandedViewService.OnIterationChanged -= HandleIterationChanged;
    }
}

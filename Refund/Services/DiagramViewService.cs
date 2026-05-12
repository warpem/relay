namespace Refund.Services;

public enum ViewMode { List, Diagram }

public class DiagramViewService
{
    public ViewMode ViewMode { get; private set; } = ViewMode.List;
    public double Zoom { get; set; } = 1.0;
    public double PanX { get; set; }
    public double PanY { get; set; }

    public event Action? OnViewModeChanged;
    public event Action? OnZoomChanged;
    public event Action? OnRelayoutRequested;

    public void SetViewMode(ViewMode mode)
    {
        if (ViewMode == mode) return;
        ViewMode = mode;
        OnViewModeChanged?.Invoke();
    }

    public void ToggleViewMode()
    {
        SetViewMode(ViewMode == ViewMode.List ? ViewMode.Diagram : ViewMode.List);
    }

    public void RequestZoomToFit()
    {
        OnZoomChanged?.Invoke();
    }

    public void RequestRelayout()
    {
        OnRelayoutRequested?.Invoke();
    }
}

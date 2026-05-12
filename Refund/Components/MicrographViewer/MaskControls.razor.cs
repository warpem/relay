using Microsoft.AspNetCore.Components;

namespace Refund.Components.MicrographViewer;

/// <summary>
/// Controls component for mask visualization and editing in the MicrographViewer.
/// Provides toggles for showing the mask, enabling mask painting, adjusting brush size,
/// and buttons for common mask operations.
/// </summary>
public partial class MaskControls : ComponentBase
{
    private bool _IsToggledShow = false;
    
    /// <summary>
    /// Controls whether the mask is visible in the micrograph viewer.
    /// Changes to this property will automatically notify the parent component.
    /// </summary>
    [Parameter]
    public bool IsToggledShow
    {
        get => _IsToggledShow;
        set
        {
            if (_IsToggledShow != value)
            {
                _IsToggledShow = value;
                OnSettingChanged();
            }
        }
    }
    
    private bool _IsToggledPaint;
    
    /// <summary>
    /// Controls whether mask painting mode is active.
    /// When enabled, allows drawing on the mask with the configured brush.
    /// Changes to this property will automatically notify the parent component.
    /// </summary>
    [Parameter]
    public bool IsToggledPaint
    {
        get => _IsToggledPaint;
        set
        {
            if (_IsToggledPaint != value)
            {
                _IsToggledPaint = value;
                OnSettingChanged();
            }
        }
    }
    
    private decimal _BrushDiameter;
    
    /// <summary>
    /// Diameter of the mask painting brush in pixels.
    /// Changes to this property will automatically notify the parent component.
    /// </summary>
    [Parameter]
    public decimal BrushDiameter
    {
        get => _BrushDiameter;
        set
        {
            if (_BrushDiameter != value)
            {
                _BrushDiameter = value;
                OnSettingChanged();
            }
        }
    }
    
    /// <summary>
    /// Event callback that fires when the "Fill" button is clicked.
    /// Typically used to fill the entire mask with the current paint value.
    /// </summary>
    [Parameter]
    public EventCallback OnFillMask { get; set; }
    
    /// <summary>
    /// Triggers the fill mask operation by invoking the OnFillMask callback.
    /// </summary>
    private void FillMask()
    {
        OnFillMask.InvokeAsync();
    }
    
    /// <summary>
    /// Event callback that fires when the "Erase" button is clicked.
    /// Typically used to clear the entire mask.
    /// </summary>
    [Parameter]
    public EventCallback OnEraseMask { get; set; }
    
    /// <summary>
    /// Triggers the erase mask operation by invoking the OnEraseMask callback.
    /// </summary>
    private void EraseMask()
    {
        OnEraseMask.InvokeAsync();
    }
    
    /// <summary>
    /// Controls whether mask editing is enabled for the current user.
    /// When false, editing controls will be disabled or hidden.
    /// </summary>
    [Parameter]
    public bool CanEditMask { get; set; }

    /// <summary>
    /// Structure that encapsulates all mask settings for passing to parent components.
    /// </summary>
    public struct MaskSettings
    {
        /// <summary>Whether the mask is currently visible.</summary>
        public bool IsShowing;
        
        /// <summary>Whether mask painting mode is active.</summary>
        public bool IsPainting;
        
        /// <summary>Current diameter of the painting brush in pixels.</summary>
        public decimal Diameter;
    }

    /// <summary>
    /// Event callback that notifies the parent component when mask settings change.
    /// </summary>
    [Parameter]
    public EventCallback<MaskSettings> OnMaskSettingsChanged { get; set; }

    /// <summary>
    /// Creates and sends a MaskSettings object with the current state to the parent component.
    /// Called whenever any of the mask settings change.
    /// </summary>
    private void OnSettingChanged()
    {
        OnMaskSettingsChanged.InvokeAsync(new MaskSettings()
        {
            IsShowing = IsToggledShow,
            IsPainting = IsToggledPaint,
            Diameter = BrushDiameter
        });
    }
}
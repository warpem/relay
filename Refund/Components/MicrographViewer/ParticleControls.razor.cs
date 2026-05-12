using Microsoft.AspNetCore.Components;

namespace Refund.Components.MicrographViewer;

/// <summary>
/// Controls component for particle visualization and picking in the MicrographViewer.
/// Provides settings for particle appearance, size, and picking mode.
/// 
/// This component is used in both MicrographViewer and TomogramViewer components to provide
/// consistent UI for particle visualization and picking functionality. It manages the particle 
/// shape display settings using the MicrographViewer.ParticleShapes enum for configuration.
/// </summary>
public partial class ParticleControls : ComponentBase
{
    /// <summary>
    /// Controls whether particle picking functionality is enabled for the current user.
    /// When false, picking controls will be disabled or hidden.
    /// </summary>
    [Parameter]
    public bool CanPickParticles { get; set; }

    private decimal _particleDiameter { set; get; }

    /// <summary>
    /// Diameter of particles in pixels for visualization and picking.
    /// Changes to this property will automatically notify the parent component.
    /// </summary>
    [Parameter]
    public decimal ParticleDiameter
    {
        get => _particleDiameter;
        set
        {
            if(_particleDiameter != value)
            {
                _particleDiameter = value;
                OnSettingChanged();
            }
        }
    }

    private decimal _particleBoxSize { set; get; }

    /// <summary>
    /// Box size of particles in pixels for extraction.
    /// This is typically larger than the particle diameter.
    /// Changes to this property will automatically notify the parent component.
    /// </summary>
    [Parameter]
    public decimal ParticleBoxSize
    {
        get => _particleBoxSize;
        set
        {
            if(_particleBoxSize != value)
            {
                _particleBoxSize = value;
                OnSettingChanged();
            }
        }
    }

    private string _particleColor;

    /// <summary>
    /// Color used for particle visualization, specified as a CSS color string.
    /// Changes to this property will automatically notify the parent component.
    /// </summary>
    [Parameter]
    public string ParticleColor
    {
        get => _particleColor;
        set
        {
            if(_particleColor != value)
            {
                _particleColor = value;
                OnSettingChanged();
            }
        }
    }

    private MicrographViewer.ParticleShapes _particleShape;

    /// <summary>
    /// Shape(s) used to display particles (Circle, Square, or Both).
    /// Changes to this property will automatically notify the parent component
    /// and update the toggle state of individual shape options.
    /// </summary>
    [Parameter]
    public MicrographViewer.ParticleShapes ParticleShape
    {
        get => _particleShape;
        set
        {
            if(_particleShape != value)
            {
                _particleShape = value;

                // Update toggle states to match the new shape flags
                if((_particleShape&MicrographViewer.ParticleShapes.Circle) != 0)
                    _isToggledCircles = true;

                if((_particleShape&MicrographViewer.ParticleShapes.Square) != 0)
                    _isToggledSquares = true;

                OnSettingChanged();
            }
        }
    }

    /// <summary>
    /// Controls whether particle display settings can be modified by the current user.
    /// When false, display controls will be disabled or hidden.
    /// </summary>
    [Parameter]
    public bool CanControlParticleDisplay { get; set; }

    private bool _isToggledPicking = false;

    /// <summary>
    /// Controls whether particle picking mode is active.
    /// When enabled, allows adding new particles by clicking on the micrograph.
    /// Changes to this property will automatically notify the parent component.
    /// </summary>
    [Parameter]
    public bool IsToggledPicking
    {
        get => _isToggledPicking;
        set
        {
            if(_isToggledPicking != value)
            {
                _isToggledPicking = value;
                OnSettingChanged();
            }
        }
    }

    private bool _isToggledCircles = false;

    /// <summary>
    /// Controls whether particles are displayed as circles.
    /// Updates the ParticleShape property with the appropriate flag.
    /// </summary>
    private bool IsToggledCircles
    {
        get => _isToggledCircles;
        set
        {
            if(_isToggledCircles != value)
            {
                _isToggledCircles = value;

                // Build a new shape value based on both toggles
                MicrographViewer.ParticleShapes result = MicrographViewer.ParticleShapes.None;

                if(_isToggledCircles)
                    result |= MicrographViewer.ParticleShapes.Circle;

                if(_isToggledSquares)
                    result |= MicrographViewer.ParticleShapes.Square;

                ParticleShape = result;
            }
        }
    }

    private bool _isToggledSquares = false;

    /// <summary>
    /// Controls whether particles are displayed as squares (boxes).
    /// Updates the ParticleShape property with the appropriate flag.
    /// </summary>
    private bool IsToggledSquares
    {
        get => _isToggledSquares;
        set
        {
            if(_isToggledSquares != value)
            {
                _isToggledSquares = value;

                // Build a new shape value based on both toggles
                MicrographViewer.ParticleShapes result = MicrographViewer.ParticleShapes.None;

                if(_isToggledCircles)
                    result |= MicrographViewer.ParticleShapes.Circle;

                if(_isToggledSquares)
                    result |= MicrographViewer.ParticleShapes.Square;

                ParticleShape = result;
            }
        }
    }

    /// <summary>
    /// Structure that encapsulates all particle settings for passing to parent components.
    /// </summary>
    public struct ParticleSettings
    {
        /// <summary>Whether particle picking mode is active.</summary>
        public bool IsPicking;
        
        /// <summary>Diameter of particles in pixels.</summary>
        public decimal Diameter;
        
        /// <summary>Box size of particles in pixels.</summary>
        public decimal BoxSize;
        
        /// <summary>Shape(s) used to display particles.</summary>
        public MicrographViewer.ParticleShapes Shape;
        
        /// <summary>Color used for particle visualization.</summary>
        public string Color;
    }

    /// <summary>
    /// Event callback that notifies the parent component when particle settings change.
    /// </summary>
    [Parameter]
    public EventCallback<ParticleSettings> OnParticleSettingsChanged { get; set; }

    /// <summary>
    /// Creates and sends a ParticleSettings object with the current state to the parent component.
    /// Called whenever any of the particle settings change.
    /// </summary>
    private void OnSettingChanged()
    {
        OnParticleSettingsChanged.InvokeAsync(new ParticleSettings()
        {
            IsPicking = IsToggledPicking,
            Diameter = ParticleDiameter,
            BoxSize = ParticleBoxSize,
            Shape = ParticleShape,
            Color = ParticleColor
        });
    }
}
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Refund.Components;

public partial class ToggleIcon : ComponentBase
{
    [Parameter] 
    public bool IsToggled { get; set; }
    
    [Parameter] 
    public EventCallback<bool> IsToggledChanged { get; set; }

    private Icon _iconToggled;
    [Parameter] 
    public Icon IconToggled { get; set; }

    private Icon _iconNeutral;
    [Parameter] 
    public Icon IconNeutral { get; set; }

    [Parameter]
    public string Style { get; set; } = "";

    [Parameter]
    public bool IsDisabled { get; set; } = false;

    protected override void OnParametersSet()
    {
        if (IconToggled != _iconToggled)
        {
            _iconToggled = IconToggled;
            _iconToggledActive = ((Icon)Activator.CreateInstance(IconToggled.GetType())).WithColor(Color.Accent);
            _iconToggledDisabled = ((Icon)Activator.CreateInstance(IconToggled.GetType())).WithColor(Color.Disabled);
        }
        
        if (IconNeutral != _iconNeutral)
        {
            _iconNeutral = IconNeutral;
            _iconNeutralActive = ((Icon)Activator.CreateInstance(IconNeutral.GetType())).WithColor(Color.Accent);
            _iconNeutralDisabled = ((Icon)Activator.CreateInstance(IconNeutral.GetType())).WithColor(Color.Disabled);
        }
        
        base.OnParametersSet();
    }

    private Appearance ButtonAppearance => IsToggled ? Appearance.Accent : Appearance.Neutral;
    
    private Icon _iconToggledActive;
    private Icon _iconToggledDisabled;
    private Icon _iconNeutralActive;
    private Icon _iconNeutralDisabled;
    private Icon IconActive => IsToggled ? _iconToggledActive : _iconNeutralActive;
    private Icon IconDisabled => IsToggled ? _iconToggledDisabled : _iconNeutralDisabled;

    private async Task Toggle()
    {
        IsToggled = !IsToggled;
        await IsToggledChanged.InvokeAsync(IsToggled);
    }
}
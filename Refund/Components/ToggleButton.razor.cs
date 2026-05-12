using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Refund.Components;

public partial class ToggleButton : ComponentBase
{
    [Parameter] 
    public bool IsToggled { get; set; }
    
    [Parameter] 
    public EventCallback<bool> IsToggledChanged { get; set; }
    
    [Parameter] 
    public RenderFragment? ChildContent { get; set; }

    private Icon _icon;
    [Parameter] 
    public Icon Icon { get; set; }

    [Parameter]
    public string Style { get; set; } = "";
    
    [Parameter]
    public string Title { get; set; } = "";

    protected override void OnParametersSet()
    {
        if (Icon != _icon)
        {
            _icon = Icon;
            IconNeutral = ((Icon)Activator.CreateInstance(Icon.GetType())).WithColor(Color.Accent);
            IconToggled = ((Icon)Activator.CreateInstance(Icon.GetType())).WithColor(Color.Fill);
        }
        
        base.OnParametersSet();
    }

    private Appearance ButtonAppearance => IsToggled ? Appearance.Accent : Appearance.Neutral;
    
    private Icon IconToggled;
    private Icon IconNeutral;
    private Icon IconColor => IsToggled ? IconToggled : IconNeutral;

    protected async Task Toggle()
    {
        IsToggled = !IsToggled;
        await IsToggledChanged.InvokeAsync(IsToggled);
    }
}
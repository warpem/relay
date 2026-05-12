using Microsoft.AspNetCore.Components;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;

namespace Relay.Screens.Main.View;

public partial class JobTypeMenuGroup : ComponentBase
{
    [Parameter, EditorRequired]
    public JobTypeGroup Group { get; set; }
    
    [Parameter]
    public ReadOnlyPortOut ClickedPort { get; set; }
    
    [Parameter]
    public MenuType Type { get; set; }
    
    [Parameter]
    public Func<Type, bool> TypeFilter { get; set; }
    
    [Parameter]
    public Func<Type, bool> PortFilter { get; set; }

    [Parameter]
    public EventCallback<Type> OnTypeSelected { get; set; }

    [Parameter]
    public EventCallback<(Type jobType, ReadOnlyPortOut portOut, ReadOnlyPortIn portIn)> OnPortSelected { get; set; }
}
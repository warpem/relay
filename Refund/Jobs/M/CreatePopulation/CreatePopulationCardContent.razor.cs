using Microsoft.AspNetCore.Components;

namespace Refund.Jobs.M.CreatePopulation;

public partial class CreatePopulationCardContent : ComponentBase
{
    [Parameter]
    public ReadOnlyCreatePopulation Job { get; set; }
}
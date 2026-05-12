using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.Services;
using Refund.Utils;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Relay.Screens.Main.View;

public partial class ViewToolbar : ComponentBase, IDisposable
{
    [Inject]
    private JobSortingService SortingService { get; set; }

    [Inject]
    private DiagramViewService DiagramService { get; set; }

    [Parameter]
    public bool CanMoveLeft { get; set; }

    [Parameter]
    public bool CanMoveRight { get; set; }

    [Parameter]
    public EventCallback OnMoveLeft { get; set; }

    [Parameter]
    public EventCallback OnMoveRight { get; set; }

    [Parameter]
    public bool IsBrowseMode { get; set; }

    private static readonly List<Option<string>> SortOptions = new()
    {
        new Option<string> { Text = "Custom", Value = nameof(JobSortCriterion.Custom) },
        new Option<string> { Text = "ID", Value = nameof(JobSortCriterion.Id) },
        new Option<string> { Text = "Last Modified", Value = nameof(JobSortCriterion.LastModified) },
        new Option<string> { Text = "Status", Value = nameof(JobSortCriterion.Status) },
        new Option<string> { Text = "Type", Value = nameof(JobSortCriterion.Type) },
        new Option<string> { Text = "Name", Value = nameof(JobSortCriterion.Name) },
        new Option<string> { Text = "Color", Value = nameof(JobSortCriterion.Color) },
    };

    private Option<string> SelectedSortOption =>
        SortOptions.FirstOrDefault(o => o.Value == SortingService.Criterion.ToString())
        ?? SortOptions[0];

    private Icon SortDirectionIcon => SortingService.IsAscending
        ? new Icons.Regular.Size16.ArrowSortUp()
        : new Icons.Regular.Size16.ArrowSortDown();

    protected override void OnInitialized()
    {
        SortingService.OnSortChanged += HandleSortChanged;
        DiagramService.OnViewModeChanged += HandleViewModeChanged;
    }

    private async Task HandleSortChanged()
    {
        await InvokeAsync(StateHasChanged);
    }

    private void HandleViewModeChanged()
    {
        InvokeAsync(StateHasChanged);
    }

    private async Task OnSortOptionChanged(Option<string>? option)
    {
        if (option?.Value != null && Enum.TryParse<JobSortCriterion>(option.Value, out var criterion))
        {
            await SortingService.SetCriterion(criterion);
        }
    }

    private async Task ToggleSortDirection()
    {
        await SortingService.ToggleDirection();
    }

    public void Dispose()
    {
        SortingService.OnSortChanged -= HandleSortChanged;
        DiagramService.OnViewModeChanged -= HandleViewModeChanged;
    }
}

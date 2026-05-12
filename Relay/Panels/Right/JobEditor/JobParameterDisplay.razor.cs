using System.Reflection;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Relay.Panels.Right.JobEditor;

/// <summary>
/// A component that displays job parameters in an organized, collapsible format.
/// </summary>
/// <remarks>
/// This component handles the core logic for displaying job parameters including:
/// - Parameter categorization and grouping
/// - Favorites section
/// - Advanced/basic mode filtering
/// - Property visibility based on dependencies
/// - Group expansion/collapse state
/// 
/// It can be used in both editable (JobEditor) and read-only (JobProperties) contexts
/// by setting the IsDisabled property.
/// </remarks>
public partial class JobParameterDisplay : ComponentBase
{
    /// <summary>
    /// Gets or sets the job whose parameters should be displayed.
    /// </summary>
    [Parameter]
    public required ReadOnlyJob Job { get; set; }

    /// <summary>
    /// Gets or sets whether the parameters should be displayed in read-only mode.
    /// </summary>
    /// <remarks>
    /// When true, all interactive elements are disabled while maintaining visual consistency.
    /// </remarks>
    [Parameter]
    public bool IsDisabled { get; set; } = false;

    /// <summary>
    /// Gets or sets whether advanced parameters should be shown.
    /// </summary>
    [Parameter]
    public bool ShowAdvanced { get; set; } = false;

    /// <summary>
    /// Gets or sets the callback that is invoked when a parameter value changes.
    /// </summary>
    [Parameter]
    public EventCallback<(PropertyInfo prop, object value)> ValueChanged { get; set; }

    /// <summary>
    /// Gets or sets whether the favorites heart buttons should be hidden.
    /// </summary>
    /// <remarks>
    /// When true, the heart toggle buttons are not rendered and the favorites section is suppressed.
    /// Used in builder mode where favorites are not relevant.
    /// </remarks>
    [Parameter]
    public bool HideFavorites { get; set; } = false;

    /// <summary>
    /// Gets or sets a function to get validation error messages for parameters.
    /// </summary>
    [Parameter]
    public required Func<string, string> GetError { get; set; }

    /// <summary>
    /// Whether to show exposure toggle buttons ("E") next to each parameter.
    /// Used in factory builder mode.
    /// </summary>
    [Parameter]
    public bool ShowExposureToggle { get; set; } = false;

    /// <summary>
    /// Function to check if a property is currently exposed.
    /// </summary>
    [Parameter]
    public Func<PropertyInfo, bool> IsPropertyExposed { get; set; }

    /// <summary>
    /// Callback invoked when a property's exposure is toggled.
    /// </summary>
    [Parameter]
    public EventCallback<(PropertyInfo prop, bool exposed)> OnExposureToggled { get; set; }

    /// <summary>
    /// Function to get the custom name for an exposed property.
    /// </summary>
    [Parameter]
    public Func<PropertyInfo, string> GetExposedPropertyName { get; set; }

    /// <summary>
    /// Callback invoked when an exposed property's custom name changes.
    /// </summary>
    [Parameter]
    public EventCallback<(PropertyInfo prop, string name)> OnExposedPropertyNameChanged { get; set; }

    /// <summary>
    /// Gets or sets the local storage service for persisting user preferences.
    /// </summary>
    [Inject]
    private ILocalStorageService LocalStorage { get; set; }

    /// <summary>
    /// List of parameters that the user has marked as favorites (stored per job type).
    /// </summary>
    private List<string> _userFavorites = new();
    
    /// <summary>
    /// List of parameter groups that the user has collapsed (stored per job type).
    /// </summary>
    private List<string> _userCollapsedGroups = new();
    
    /// <summary>
    /// Icon for the filled heart (favorite) button.
    /// </summary>
    private Icon iconHeartFilled = new Icons.Filled.Size16.Heart();

    /// <summary>
    /// Icon for the regular heart (not favorite) button.
    /// </summary>
    private Icon iconHeartRegular = new Icons.Regular.Size16.Heart();

    private Icon iconExposedFilled = new Icons.Filled.Size16.ArrowCircleUpRight();
    private Icon iconExposedRegular = new Icons.Regular.Size16.ArrowCircleUpRight();

    /// <summary>
    /// Initializes the component and loads user preferences.
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        if (Job != null)
        {
            await GetUserSettings();
        }
    }

    /// <summary>
    /// Handles parameter value changes and propagates them to the parent component.
    /// </summary>
    /// <param name="args">A tuple containing the property and its new value</param>
    private async Task HandleParameterChanged((PropertyInfo prop, object value) args)
    {
        await ValueChanged.InvokeAsync(args);
    }

    /// <summary>
    /// Determines if a property should be visible based on dependency conditions.
    /// </summary>
    /// <param name="property">The property to check</param>
    /// <returns>True if the property should be visible, false otherwise</returns>
    private bool IsPropertyVisible(PropertyInfo property)
    {
        if (Job == null)
            return true;
            
        return Refund.DataModel.Job.IsPropertyVisible(Job.GetOriginalType(), property, Job);
    }

    /// <summary>
    /// Handles toggling a parameter's favorite status.
    /// </summary>
    /// <param name="parameterName">The name of the parameter to toggle</param>
    private async Task HandleFavoriteToggle(string parameterName)
    {
        if (IsFavorite(parameterName))
            await RemoveFavorite(parameterName);
        else
            await AddFavorite(parameterName);
    }

    /// <summary>
    /// Handles group expansion changes.
    /// </summary>
    /// <param name="groupName">The name of the group being expanded/collapsed</param>
    /// <param name="isExpanded">Whether the group is being expanded or collapsed</param>
    private async Task HandleGroupExpandedChanged(string groupName, bool isExpanded)
    {
        if (isExpanded)
            await RemoveCollapsedGroup(groupName);
        else
            await AddCollapsedGroup(groupName);
    }
    
    #region User Preferences

    /// <summary>
    /// Loads user preferences for the current job type from local storage.
    /// </summary>
    private async Task GetUserSettings()
    {
        if (Job == null) return;
        
        _userFavorites = await LocalStorage.GetItemAsync<List<string>>(Job.GetOriginalType() + ".favorites") ?? [];
        _userCollapsedGroups = await LocalStorage.GetItemAsync<List<string>>(Job.GetOriginalType() + ".collapsed") ?? [];
    }
    
    /// <summary>
    /// Checks if a parameter is in the user's favorites list.
    /// </summary>
    /// <param name="name">The parameter name to check</param>
    /// <returns>True if the parameter is a favorite, false otherwise</returns>
    private bool IsFavorite(string name) => _userFavorites.Contains(name);

    /// <summary>
    /// Checks if a parameter group is collapsed.
    /// </summary>
    /// <param name="name">The group name to check</param>
    /// <returns>True if the group is collapsed, false otherwise</returns>
    private bool IsGroupCollapsed(string name) => _userCollapsedGroups.Contains(name);
    
    /// <summary>
    /// Adds a parameter to the user's favorites list.
    /// </summary>
    /// <param name="name">The parameter name to favorite</param>
    private async Task AddFavorite(string name)
    {
        if (!_userFavorites.Contains(name))
        {
            _userFavorites.Add(name);
            await LocalStorage.SetItemAsync(Job.GetOriginalType() + ".favorites", _userFavorites);
            StateHasChanged();
        }
    }

    /// <summary>
    /// Removes a parameter from the user's favorites list.
    /// </summary>
    /// <param name="name">The parameter name to unfavorite</param>
    private async Task RemoveFavorite(string name)
    {
        if (_userFavorites.Contains(name))
        {
            _userFavorites.Remove(name);
            await LocalStorage.SetItemAsync(Job.GetOriginalType() + ".favorites", _userFavorites);
            StateHasChanged();
        }
    }

    /// <summary>
    /// Collapses a parameter group in the UI.
    /// </summary>
    /// <param name="name">The group name to collapse</param>
    private async Task AddCollapsedGroup(string name)
    {
        if (!_userCollapsedGroups.Contains(name))
        {
            _userCollapsedGroups.Add(name);
            await LocalStorage.SetItemAsync(Job.GetOriginalType() + ".collapsed", _userCollapsedGroups);
        }
    }

    /// <summary>
    /// Expands a parameter group in the UI.
    /// </summary>
    /// <param name="name">The group name to expand</param>
    private async Task RemoveCollapsedGroup(string name)
    {
        if (_userCollapsedGroups.Contains(name))
        {
            _userCollapsedGroups.Remove(name);
            await LocalStorage.SetItemAsync(Job.GetOriginalType() + ".collapsed", _userCollapsedGroups);
        }
    }
    
    #endregion
}
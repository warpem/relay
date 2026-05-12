using System.Reflection;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.UIFields;
using FEmoji = Microsoft.FluentUI.AspNetCore.Components.Emoji;
using Emojis = Microsoft.FluentUI.AspNetCore.Components.Emojis;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Relay.Panels.Right.JobEditor;

/// <summary>
/// A component that provides a form for editing job parameters.
/// </summary>
/// <remarks>
/// The JobEditor component is the main interface for configuring job parameters. It displays
/// a form with all editable properties of a job, organized into collapsible parameter groups.
/// 
/// Features include:
/// - Input validation with error messages
/// - User preference persistence (favorites, collapsed groups, advanced mode)
/// - Port connection management
/// - Job alias editing
/// - Queue selection and job submission
/// 
/// The component dynamically renders appropriate input fields for each job parameter based on 
/// its UIField attributes, handling different data types and constraints appropriately.
/// </remarks>
public partial class JobEditor : ComponentBase, IDisposable
{
    /// <summary>
    /// The job currently being edited.
    /// </summary>
    private ReadOnlyJob _job;
    
    /// <summary>
    /// List of parameter groups that the user has collapsed (stored per job type).
    /// </summary>
    private List<string> _userCollapsedGroups = new();
    
    /// <summary>
    /// List of parameters that the user has marked as favorites (stored per job type).
    /// </summary>
    private List<string> _userFavorites = new();
    
    /// <summary>
    /// Whether the user has chosen to show advanced parameters (stored per job type).
    /// </summary>
    private bool? _userShowAdvanced = false;
    
    /// <summary>
    /// Dictionary of validation errors for job parameters, keyed by property name.
    /// </summary>
    private Dictionary<string, string> _validationErrors = new();
    
    /// <summary>
    /// Dictionary of validation errors for ports, keyed by port name with list of error messages.
    /// </summary>
    private Dictionary<string, List<string>> _portValidationErrors = new();
    
    /// <summary>
    /// Gets or sets the current session context.
    /// </summary>
    [Inject]
    private RelaySession Session { get; set; }
    
    /// <summary>
    /// Gets or sets the data manager service for performing operations on the job.
    /// </summary>
    [Inject]
    private DataManager DataManager { get; set; }
    
    /// <summary>
    /// Gets or sets the job editor service that manages the current job being edited.
    /// </summary>
    [Inject]
    private JobEditorService Editor { get; set; }

    /// <summary>
    /// Gets or sets the local storage service for persisting user preferences.
    /// </summary>
    [Inject]
    private ILocalStorageService LocalStorage { get; set; }
    
    /// <summary>
    /// Gets or sets the toast service for showing notifications.
    /// </summary>
    [Inject]
    private IToastService ToastService { get; set; }

    
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
    /// Emoji for basic mode in light theme.
    /// </summary>
    private FEmoji emojiBabyFaceLight = new Emojis.PeopleBody.Color.Default.Baby();
    
    /// <summary>
    /// Emoji for advanced mode in light theme.
    /// </summary>
    private FEmoji emojiNerdFaceLight = new Emojis.SmileysEmotion.Color.Default.SmilingFaceWithSunglasses();
    
    /// <summary>
    /// Emoji for basic mode in dark theme.
    /// </summary>
    private FEmoji emojiBabyFaceDark = new Emojis.SmileysEmotion.Color.Default.Ghost();
    
    /// <summary>
    /// Emoji for advanced mode in dark theme.
    /// </summary>
    private FEmoji emojiNerdFaceDark = new Emojis.SmileysEmotion.Color.Default.Ogre();

    /// <summary>
    /// Initializes the component and sets up event subscriptions.
    /// </summary>
    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();

        Session.OnThemeChanged += HandleThemeChanged;
        Editor.OnJobChanged += HandleJobChanged;
        Editor.OnJobUpdated += HandleJobUpdated;

        await HandleJobChanged(Editor.CurrentJob);
    }

    /// <summary>
    /// Handles theme change events from the session.
    /// </summary>
    private async Task HandleThemeChanged()
    {
        await InvokeAsync(StateHasChanged);
    }
    
    /// <summary>
    /// Handles job change events from the editor service.
    /// </summary>
    /// <param name="job">The new job being edited</param>
    private async Task HandleJobChanged(ReadOnlyJob job)
    {
        if (_job != job)
        {
            _job = job;
            _validationErrors.Clear();
            _portValidationErrors.Clear();
            _definitionSubscription?.Unsubscribe();
            _definitionSubscription = null;

            if (_job != null)
            {
                await GetUserSettings();
                await HandleJobUpdated(job);

                // In builder mode, subscribe to definition updates so the editor
                // refreshes when internal edges change (e.g. from "Connect to" menu)
                if (IsBuilderMode && Session.FactoryDefinition != null)
                {
                    _definitionSubscription = DataManager.FactoryDefinitionUpdated.Add(
                        GroupName.FactoryDefinition(Session.Project.Id, Session.Space.Id, Session.FactoryDefinition.Id),
                        async _ => await HandleJobUpdated(_job));
                }
            }
        }
    }

    private GroupEventSubscription _definitionSubscription;

    /// <summary>
    /// Handles job update events from the editor service.
    /// </summary>
    /// <param name="job">The updated job</param>
    /// <remarks>
    /// Re-validates job parameters after updates and clears validation errors for hidden fields.
    /// </remarks>
    private async Task HandleJobUpdated(ReadOnlyJob job)
    {
        _validationErrors = _job.ValidateInputs();
        _portValidationErrors = _job.ValidatePortInputs();

        // In builder mode, clear port validation errors for ports with internal or external connections
        if (IsBuilderMode)
        {
            foreach (var port in _job.PortsIn.Values)
            {
                var internalConns = GetInternalConnectionsForPort(port.Name);
                var externalConns = GetExternalConnectionsForPort(port.Name);
                if (internalConns.Count > 0 || externalConns.Count > 0)
                    _portValidationErrors.Remove(port.Name);
            }
        }

        // Clear validation errors for hidden fields due to dependencies
        var hiddenProperties = Job.TypeParameters[_job.GetOriginalType()]
            .Where(prop => !IsPropertyVisible(prop))
            .Select(prop => prop.Name)
            .ToList();

        foreach (var propName in hiddenProperties)
        {
            _validationErrors.Remove(propName);
        }

        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Updates the job's alias when it's changed in the UI.
    /// </summary>
    /// <param name="alias">The new alias</param>
    private async Task HandleJobAliasChanged(string alias)
    {
        if (alias == null || alias == _job.Alias)
            return;

        if (IsBuilderMode)
        {
            await UpdateBlueprintSubJob(subJob => subJob.Alias = alias);
        }
        else
        {
            await DataManager.UpdateJob(Session.User,
                                        Editor.CurrentJob,
                                        job =>
                                        {
                                            job.Alias = alias;
                                        });
        }
    }
    

    /// <summary>
    /// Removes a connection from an input port.
    /// </summary>
    /// <param name="edge">The edge to remove</param>
    private async Task HandlePortEdgeRemoved(ReadOnlyEdge edge)
    {
        if (edge == null)
            return;

        await DataManager.DeleteEdge(edge);
    }

    /// <summary>
    /// Updates a job parameter value when changed in the UI.
    /// </summary>
    /// <param name="args">A tuple containing the property and its new value</param>
    private async Task HandleParameterChanged((PropertyInfo prop, object value) args)
    {
        if (Equals(args.value, _job.GetParameterValue(args.prop)))
            return;

        if (IsBuilderMode)
        {
            await UpdateBlueprintSubJob(subJob => args.prop.SetValue(subJob, args.value));
        }
        else
        {
            await DataManager.UpdateJob(Session.User, _job,
                                        originalJob =>
                                        {
                                            args.prop.SetValue(originalJob, args.value);
                                        });
        }
    }

    /// <summary>
    /// Submits the job to the local queue for execution.
    /// </summary>
    private async Task HandleLocalQueueSelected()
    {
        try
        {
            await DataManager.QueueLocalJob(Session.User, _job);
            await Editor.SetJob(null);
        }
        catch(Exception ex)
        {
            ToastService.ShowError($"Failed to queue job: {ex.Message}");
        }
    }

    /// <summary>
    /// Submits the job to a cluster queue for execution.
    /// </summary>
    /// <param name="queue">The cluster queue to submit to</param>
    private async Task HandleClusterQueueSelected(ReadOnlyJobQueue queue)
    {
        try
        {
            if (queue != null)
            {
                await DataManager.QueueClusterJob(Session.User, _job, queue);
                await Editor.SetJob(null);
            }
            else
            {
                throw new Exception($"Couldn't find queue with ID {queue.Id}");
            }
        }
        catch (Exception ex)
        {
            ToastService.ShowError($"Failed to queue job: {ex.Message}");
        }
    }

    private bool IsBuilderMode => Session.CurrentMain == MainScreenType.FactoryBuilder;

    private bool IsFactoryInstanceSubJob => Session.FactoryInstance != null;

    private string GetUiFieldLabel(PropertyInfo prop)
    {
        if (_job == null) return prop.Name;
        var jobType = _job.GetOriginalType();
        if (Job.TypeUiFields.TryGetValue(jobType, out var fields) && fields.TryGetValue(prop, out var uiField))
            return uiField.Label ?? prop.Name;
        return prop.Name;
    }

    /// <summary>
    /// Handles input port exposure toggle from JobPortDisplay.
    /// </summary>
    private async Task HandleInputPortExposureToggled((ReadOnlyPortIn port, bool exposed) args)
    {
        await ToggleInputPortExposure(args.port, args.exposed);
    }

    /// <summary>
    /// Removes an internal edge in the factory definition.
    /// </summary>
    private async Task HandleInternalEdgeRemoved((string portName, int sourceJobId, string sourcePortName) args)
    {
        var def = Session.FactoryDefinition;
        if (def == null || _job == null) return;

        try
        {
            var sourceKey = $"{args.sourceJobId}.{args.sourcePortName}";
            var targetKey = $"{_job.Id}.{args.portName}";

            await DataManager.UpdateFactoryDefinition(Session.User, Session.Space, def, d =>
            {
                d.InternalEdges.RemoveAll(e => e.Source == sourceKey && e.Target == targetKey);
            });

            // Refresh validation
            await HandleJobUpdated(_job);
        }
        catch (Exception exc)
        {
            ToastService.ShowError($"Couldn't remove connection: {exc.Message}");
        }
    }

    /// <summary>
    /// Removes an external edge in the factory definition.
    /// </summary>
    private async Task HandleExternalEdgeRemoved((string portName, int externalJobId, string externalPort) args)
    {
        var def = Session.FactoryDefinition;
        if (def == null || _job == null) return;

        try
        {
            await DataManager.UpdateFactoryDefinition(Session.User, Session.Space, def, d =>
            {
                d.ExternalEdges.RemoveAll(e =>
                    e.SubJobId == _job.Id &&
                    e.SubJobPort == args.portName &&
                    e.ExternalJobId == args.externalJobId &&
                    e.ExternalPort == args.externalPort);
            });

            await HandleJobUpdated(_job);
        }
        catch (Exception exc)
        {
            ToastService.ShowError($"Couldn't remove external connection: {exc.Message}");
        }
    }

    /// <summary>
    /// Returns external edge connections targeting a specific port on the current sub-job.
    /// </summary>
    private List<(int externalJobId, string externalPort)> GetExternalConnectionsForPort(string portName)
    {
        var def = Session.FactoryDefinition;
        if (def == null || _job == null) return new();

        return def.ExternalEdges
            .Where(e => e.SubJobId == _job.Id && e.SubJobPort == portName)
            .Select(e => (e.ExternalJobId, e.ExternalPort))
            .ToList();
    }

    /// <summary>
    /// Returns internal edge connections targeting a specific port on the current sub-job.
    /// </summary>
    private List<(int sourceJobId, string sourcePortName)> GetInternalConnectionsForPort(string portName)
    {
        var def = Session.FactoryDefinition;
        if (def == null || _job == null) return new();

        var targetKey = $"{_job.Id}.{portName}";
        var result = new List<(int, string)>();

        foreach (var edge in def.InternalEdges)
        {
            if (edge.Target == targetKey)
            {
                var dotIndex = edge.Source.IndexOf('.');
                if (dotIndex > 0 &&
                    int.TryParse(edge.Source[..dotIndex], out var srcId))
                {
                    result.Add((srcId, edge.Source[(dotIndex + 1)..]));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Updates a blueprint sub-job within the factory definition (used in builder mode).
    /// </summary>
    private async Task UpdateBlueprintSubJob(Action<Job> updateAction)
    {
        var def = Session.FactoryDefinition;
        if (def == null || _job == null) return;

        await DataManager.UpdateFactoryDefinition(Session.User, Session.Space, def, d =>
        {
            var subJob = d.SubJobs.FirstOrDefault(j => j.Id == _job.Id);
            if (subJob != null)
                updateAction(subJob);
        });
    }

    private async Task HandleExposureToggled((PropertyInfo prop, bool exposed) args)
    {
        await TogglePropertyExposure(args.prop, args.exposed);
    }

    private async Task HandleExposedInputPortNameChanged((ReadOnlyPortIn port, string name) args)
    {
        await SetExposedInputPortName(args.port, args.name);
    }

    private async Task HandleExposedPropertyNameChanged((PropertyInfo prop, string name) args)
    {
        await SetExposedPropertyName(args.prop, args.name);
    }

    private bool IsPropertyExposed(PropertyInfo prop)
    {
        var def = Session.FactoryDefinition;
        if (def == null || _job == null) return false;
        return def.ExposedProperties.Any(ep =>
            ep.SubJobId == _job.Id && ep.PropertyName == prop.Name);
    }

    private string GetExposedPropertyName(PropertyInfo prop)
    {
        var def = Session.FactoryDefinition;
        if (def == null || _job == null) return "";
        return def.ExposedProperties
            .FirstOrDefault(ep => ep.SubJobId == _job.Id && ep.PropertyName == prop.Name)
            ?.CustomName ?? "";
    }

    private async Task TogglePropertyExposure(PropertyInfo prop, bool exposed)
    {
        var def = Session.FactoryDefinition;
        if (def == null || _job == null) return;

        try
        {
            await DataManager.UpdateFactoryDefinition(Session.User, Session.Space, def, d =>
            {
                if (exposed)
                {
                    var uiField = prop.GetCustomAttribute<UiFieldBase>();
                    d.ExposedProperties.Add(new ExposedProperty
                    {
                        SubJobId = _job.Id,
                        PropertyName = prop.Name,
                        CustomName = uiField?.Label ?? prop.Name
                    });
                }
                else
                {
                    d.ExposedProperties.RemoveAll(ep =>
                        ep.SubJobId == _job.Id && ep.PropertyName == prop.Name);
                }
            });
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception exc)
        {
            ToastService.ShowError($"Couldn't update exposure: {exc.Message}");
        }
    }

    private async Task SetExposedPropertyName(PropertyInfo prop, string name)
    {
        var def = Session.FactoryDefinition;
        if (def == null || _job == null) return;

        try
        {
            await DataManager.UpdateFactoryDefinition(Session.User, Session.Space, def, d =>
            {
                var ep = d.ExposedProperties.FirstOrDefault(ep =>
                    ep.SubJobId == _job.Id && ep.PropertyName == prop.Name);
                if (ep != null)
                    ep.CustomName = name;
            });
        }
        catch (Exception exc)
        {
            ToastService.ShowError($"Couldn't update name: {exc.Message}");
        }
    }

    private string GetExposedInputPortName(ReadOnlyPortIn port)
    {
        var def = Session.FactoryDefinition;
        if (def == null || _job == null) return "";
        return def.ExposedPortsIn
            .FirstOrDefault(ep => ep.SubJobId == _job.Id && ep.PortName == port.Name)
            ?.CustomName ?? "";
    }

    private async Task SetExposedInputPortName(ReadOnlyPortIn port, string name)
    {
        var def = Session.FactoryDefinition;
        if (def == null || _job == null) return;

        try
        {
            await DataManager.UpdateFactoryDefinition(Session.User, Session.Space, def, d =>
            {
                var ep = d.ExposedPortsIn.FirstOrDefault(ep =>
                    ep.SubJobId == _job.Id && ep.PortName == port.Name);
                if (ep != null)
                    ep.CustomName = name;
            });
        }
        catch (Exception exc)
        {
            ToastService.ShowError($"Couldn't update port name: {exc.Message}");
        }
    }

    private string GetExposedOutputPortName(ReadOnlyPortOut port)
    {
        var def = Session.FactoryDefinition;
        if (def == null || _job == null) return "";
        return def.ExposedPortsOut
            .FirstOrDefault(ep => ep.SubJobId == _job.Id && ep.PortName == port.Name)
            ?.CustomName ?? "";
    }

    private async Task SetExposedOutputPortName(ReadOnlyPortOut port, string name)
    {
        var def = Session.FactoryDefinition;
        if (def == null || _job == null) return;

        try
        {
            await DataManager.UpdateFactoryDefinition(Session.User, Session.Space, def, d =>
            {
                var ep = d.ExposedPortsOut.FirstOrDefault(ep =>
                    ep.SubJobId == _job.Id && ep.PortName == port.Name);
                if (ep != null)
                    ep.CustomName = name;
            });
        }
        catch (Exception exc)
        {
            ToastService.ShowError($"Couldn't update port name: {exc.Message}");
        }
    }

    private bool IsInputPortExposed(ReadOnlyPortIn port)
    {
        var def = Session.FactoryDefinition;
        if (def == null || _job == null) return false;
        return def.ExposedPortsIn.Any(ep =>
            ep.SubJobId == _job.Id && ep.PortName == port.Name);
    }

    private bool IsOutputPortExposed(ReadOnlyPortOut port)
    {
        var def = Session.FactoryDefinition;
        if (def == null || _job == null) return false;
        return def.ExposedPortsOut.Any(ep =>
            ep.SubJobId == _job.Id && ep.PortName == port.Name);
    }

    private async Task ToggleInputPortExposure(ReadOnlyPortIn port, bool exposed)
    {
        var def = Session.FactoryDefinition;
        if (def == null || _job == null) return;

        try
        {
            await DataManager.UpdateFactoryDefinition(Session.User, Session.Space, def, d =>
            {
                if (exposed)
                {
                    d.ExposedPortsIn.Add(new ExposedPort
                    {
                        SubJobId = _job.Id,
                        PortName = port.Name,
                        CustomName = port.Alias,
                        ResourceType = port.ResourceType.Name
                    });
                }
                else
                {
                    d.ExposedPortsIn.RemoveAll(ep =>
                        ep.SubJobId == _job.Id && ep.PortName == port.Name);
                }
            });
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception exc)
        {
            ToastService.ShowError($"Couldn't update port exposure: {exc.Message}");
        }
    }

    private async Task ToggleOutputPortExposure(ReadOnlyPortOut port, bool exposed)
    {
        var def = Session.FactoryDefinition;
        if (def == null || _job == null) return;

        try
        {
            await DataManager.UpdateFactoryDefinition(Session.User, Session.Space, def, d =>
            {
                if (exposed)
                {
                    d.ExposedPortsOut.Add(new ExposedPort
                    {
                        SubJobId = _job.Id,
                        PortName = port.Name,
                        CustomName = port.Alias,
                        ResourceType = port.ResourceType.Name
                    });
                }
                else
                {
                    d.ExposedPortsOut.RemoveAll(ep =>
                        ep.SubJobId == _job.Id && ep.PortName == port.Name);
                }
            });
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception exc)
        {
            ToastService.ShowError($"Couldn't update port exposure: {exc.Message}");
        }
    }

    /// <summary>
    /// Cleans up event subscriptions when the component is disposed.
    /// </summary>
    public void Dispose()
    {
        _definitionSubscription?.Unsubscribe();
        Editor.OnJobChanged -= HandleJobChanged;
        Editor.OnJobUpdated -= HandleJobUpdated;
        Session.OnThemeChanged -= HandleThemeChanged;
    }
    
    #region User settings

    /// <summary>
    /// Loads user preferences for the current job type from local storage.
    /// </summary>
    private async Task GetUserSettings()
    {
        _userFavorites = await LocalStorage.GetItemAsync<List<string>>(_job.GetOriginalType() + ".favorites") ?? [];

        _userCollapsedGroups = await LocalStorage.GetItemAsync<List<string>>(_job.GetOriginalType() + ".collapsed") ?? [];

        _userShowAdvanced = await LocalStorage.GetItemAsync<bool?>(_job.GetOriginalType() + ".showAdvanced") ?? false;
    }
    
    /// <summary>
    /// Adds a parameter to the user's favorites list.
    /// </summary>
    /// <param name="name">The parameter name to favorite</param>
    public async Task AddFavorite(string name)
    {
        if(!_userFavorites.Contains(name))
        {
            _userFavorites.Add(name);
            await LocalStorage.SetItemAsync(_job.GetOriginalType() + ".favorites", _userFavorites);

            StateHasChanged();
        }
    }

    /// <summary>
    /// Removes a parameter from the user's favorites list.
    /// </summary>
    /// <param name="name">The parameter name to unfavorite</param>
    public async Task RemoveFavorite(string name)
    {
        if(_userFavorites.Contains(name))
        {
            _userFavorites.Remove(name);
            await LocalStorage.SetItemAsync(_job.GetOriginalType() + ".favorites", _userFavorites);

            StateHasChanged();
        }
    }

    /// <summary>
    /// Collapses a parameter group in the UI.
    /// </summary>
    /// <param name="name">The group name to collapse</param>
    public async Task AddCollapsedGroup(string name)
    {
        if(!_userCollapsedGroups.Contains(name))
        {
            _userCollapsedGroups.Add(name);
            await LocalStorage.SetItemAsync(_job.GetOriginalType() + ".collapsed", _userCollapsedGroups);
        }
    }

    /// <summary>
    /// Expands a parameter group in the UI.
    /// </summary>
    /// <param name="name">The group name to expand</param>
    public async Task RemoveCollapsedGroup(string name)
    {
        if(_userCollapsedGroups.Contains(name))
        {
            _userCollapsedGroups.Remove(name);
            await LocalStorage.SetItemAsync(_job.GetOriginalType() + ".collapsed", _userCollapsedGroups);
        }
    }

    /// <summary>
    /// Toggles the display of advanced parameters.
    /// </summary>
    /// <param name="showAdvanced">Whether to show advanced parameters</param>
    public async Task SetShowAdvanced(bool showAdvanced)
    {
        _userShowAdvanced = showAdvanced;
        await LocalStorage.SetItemAsync(_job.GetOriginalType() + ".showAdvanced", _userShowAdvanced);
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Checks if a parameter is in the user's favorites list.
    /// </summary>
    /// <param name="name">The parameter name to check</param>
    /// <returns>True if the parameter is a favorite, false otherwise</returns>
    public bool IsFavorite(string name) => _userFavorites.Contains(name);

    /// <summary>
    /// Checks if a parameter group is collapsed.
    /// </summary>
    /// <param name="name">The group name to check</param>
    /// <returns>True if the group is collapsed, false otherwise</returns>
    public bool IsGroupCollapsed(string name) => _userCollapsedGroups.Contains(name);
    
    #endregion
    
    #region Dependencies
    
    /// <summary>
    /// Determines if a property should be visible based on dependency conditions.
    /// </summary>
    /// <param name="property">The property to check</param>
    /// <returns>True if the property should be visible, false otherwise</returns>
    private bool IsPropertyVisible(PropertyInfo property)
    {
        if (_job == null)
            return true;
            
        return Job.IsPropertyVisible(_job.GetOriginalType(), property, _job);
    }
    
    #endregion
    
    #region Validation

    /// <summary>
    /// Gets the validation error message for a parameter, if any.
    /// </summary>
    /// <param name="propertyName">The parameter name to get the error for</param>
    /// <returns>The error message or an empty string if there is no error</returns>
    private string GetError(string propertyName) => _validationErrors.ContainsKey(propertyName) ? 
                                                        _validationErrors[propertyName] : 
                                                        string.Empty;
    
    /// <summary>
    /// Gets the validation error messages for a port, if any.
    /// </summary>
    /// <param name="portName">The port name to get the errors for</param>
    /// <returns>The list of error messages or an empty list if there are no errors</returns>
    private List<string> GetPortErrors(string portName) => _portValidationErrors.ContainsKey(portName) ? 
                                                               _portValidationErrors[portName] : 
                                                               new List<string>();
    
    /// <summary>
    /// Checks if the job has any validation errors (both UIField and port validation).
    /// Port validation already filters out inactive ports, so we don't need to check that here.
    /// </summary>
    /// <returns>True if there are any validation errors, false otherwise</returns>
    private bool HasAnyValidationErrors() => _validationErrors.Any() || _portValidationErrors.Any();
    
    #endregion
}
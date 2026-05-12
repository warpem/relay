using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Serilog;
using Refund.DataModel;

namespace Refund.Services.Core.Repositories;

/// <summary>
/// Core data storage and management service that handles atomic operations on the data model and persistence to disk.
/// Manages projects, spaces, jobs, edges, and views with thread-safe CRUD operations, supporting auto-saving functionality.
/// </summary>
public class DataRepository : IDisposable
{
    /// <summary>
    /// The file path where projects are stored.
    /// </summary>
    private readonly string _projectsPath;
    
    /// <summary>
    /// JSON serialization options used for reading/writing data.
    /// </summary>
    private readonly JsonSerializerOptions _jsonOptions;
    
    /// <summary>
    /// Logger for this repository
    /// </summary>
    private readonly ILogger _logger = Log.ForContext<DataRepository>();
    
    // Core data collection
    private readonly List<Project> _projects = new();
    
    // Synchronization
    private readonly object _saveLock = new();
    
    // Auto save
    private Timer _autoSaveTimer;
    private int _autoSaveInterval;
    private bool _disposed;
    private readonly HashSet<Project> _pendingUpdateProjects = new();
    private readonly HashSet<Space> _pendingUpdateSpaces = new();

    /// <summary>
    /// Read-only collection of all projects.
    /// </summary>
    public ReadOnlyCollection<Project> Projects => _projects.AsReadOnly();

    /// <summary>
    /// Initializes a new instance of DataRepository,
    /// but does not load any projects or start auto-saving.
    /// </summary>
    /// <param name="projectsPath">The path to the projects file.</param>
    public DataRepository(string projectsPath)
    {
        _projectsPath = projectsPath;
        
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        _jsonOptions.MakeReadOnly();
    }

    #region Autosave changes

    /// <summary>
    /// Starts periodically saving changes to projects and spaces.
    /// </summary>
    /// <param name="milliseconds">The interval, in milliseconds, at which to save changes.</param>
    public void StartAutoSave(int milliseconds)
    {
        _autoSaveInterval = milliseconds;
        _autoSaveTimer = new Timer(SaveChanges, null, _autoSaveInterval, Timeout.Infinite);
    }

    /// <summary>
    /// Stops periodically saving changes to projects and spaces.
    /// </summary>
    public void StopAutoSave()
    {
        _autoSaveTimer.Dispose();
    }

    #endregion

    #region Persistence

    /// <summary>
    /// Loads all projects and their spaces from persistent storage.
    /// </summary>
    public void LoadAll(ReadOnlyCollection<User> users)
    {
        lock (_saveLock)
        {
            LoadProjects(users);
            foreach (var project in _projects)
                project.LoadSpaces(users);
        }
    }

    private void LoadProjects(ReadOnlyCollection<User> users)
    {
        if (!File.Exists(_projectsPath))
        {
            _logger.Information("No previous project data found at {ProjectsPath}", Path.GetFullPath(_projectsPath));
            return;
        }

        var projectsString = File.ReadAllText(_projectsPath);
        var projectsNode = JsonNode.Parse(projectsString);

        if (projectsNode == null)
            throw new Exception($"Couldn't parse JSON from {Path.GetFullPath(_projectsPath)}");

        if (projectsNode["Projects"] != null)
        {
            _projects.Clear();
            projectsNode["Projects"].Deserialize<List<JsonObject>>()?.ForEach(p =>
            {
                var loadedProject = new Project();
                loadedProject.ReadFromJson(p, users);
                _projects.Add(loadedProject);
            });
        }
    }

    private void SaveChanges(object? state)
    {
        lock (_saveLock)
        {
            try
            {
                if (_pendingUpdateProjects.Count > 0)
                {
                    SaveProjects();
                    _logger.Debug("Saved {ProjectCount} projects", _pendingUpdateProjects.Count);
                    _pendingUpdateProjects.Clear();
                }

                foreach (var space in _pendingUpdateSpaces)
                {
                    SaveSpace(space);
                    _logger.Debug("Saved space {SpaceName}", space.QualifiedName);
                }

                _pendingUpdateSpaces.Clear();
            }
            catch (Exception e)
            {
                _logger.Error(e, "Error saving changes");
            }
            finally
            {
                if (!_disposed)
                    _autoSaveTimer.Change(_autoSaveInterval, Timeout.Infinite);
            }
        }
    }

    private void SaveProjects()
    {
        var directoryPath = Path.GetDirectoryName(_projectsPath);
        if (!string.IsNullOrWhiteSpace(directoryPath) && !Directory.Exists(directoryPath))
            Directory.CreateDirectory(directoryPath);

        var projectsJson = new JsonObject();
        projectsJson["Projects"] = new JsonArray(_projects.Select(p =>
        {
            var writer = new JsonObject();
            p.WriteToJson(writer);
            return writer;
        }).ToArray<JsonNode>());

        File.WriteAllText(_projectsPath, projectsJson.ToJsonString(_jsonOptions));
    }

    private void SaveSpace(Space space)
    {
        var directoryPath = Path.GetDirectoryName(space.FilePath);
        if (!string.IsNullOrWhiteSpace(directoryPath) && !Directory.Exists(directoryPath))
            Directory.CreateDirectory(directoryPath);

        var tempPath = Path.Combine(space.RootDirectory, Path.GetRandomFileName());
        var spaceJson = new JsonObject();
        space.WriteToJson(spaceJson);
        File.WriteAllText(tempPath, spaceJson.ToJsonString(_jsonOptions));

        File.Move(tempPath, space.FilePath, true);
    }

    #endregion

    #region Project Operations

    /// <summary>
    /// Creates a new project in the repository, optionally based on a template project.
    /// Sets the ID, creation/update metadata, and adds the project to the pending updates list for auto-saving.
    /// </summary>
    /// <param name="user">The user creating the project, who will be set as the owner and creator</param>
    /// <param name="template">Optional template project to copy properties from</param>
    /// <returns>The newly created project</returns>
    public Project CreateProject(User user, Project template = null)
    {
        lock (_saveLock)
        {
            var project = new Project();
            if (template != null)
                project.AdoptState(template);

            project.Id = _projects.Select(p => p.Id).DefaultIfEmpty(0).Max() + 1;
            _projects.Add(project);
            
            project.CreationDate = DateTime.Now;
            project.CreatedBy = user;
            project.UpdateDate = DateTime.Now;
            project.UpdatedBy = user;
            project.Owner = user;
            
            _pendingUpdateProjects.Add(project);

            _logger.Information("Created project {ProjectId} by user {UserId}", project.Id, user.Id);
            return project;
        }
    }

    /// <summary>
    /// Updates an existing project by applying the specified action to it.
    /// Updates the modification metadata and marks the project for auto-saving.
    /// </summary>
    /// <param name="user">The user performing the update</param>
    /// <param name="project">The project to update</param>
    /// <param name="updateAction">Action that performs the actual modifications to the project</param>
    /// <exception cref="ArgumentNullException">Thrown if project is null</exception>
    public void UpdateProject(User user, Project project, Action<Project> updateAction)
    {
        if (project == null) throw new ArgumentNullException(nameof(project));

        lock (_saveLock)
        {
            if (updateAction != null)
                updateAction(project);
            project.UpdateDate = DateTime.Now;
            project.UpdatedBy = user;
            
            _pendingUpdateProjects.Add(project);
            
            _logger.Debug("Updated project {ProjectId} by user {UserId}", project.Id, user.Id);
        }
    }

    /// <summary>
    /// Removes a project from the repository and marks it for deletion in the next auto-save cycle.
    /// Note that this doesn't delete the project data from disk immediately - that happens during the auto-save.
    /// </summary>
    /// <param name="project">The project to delete</param>
    /// <exception cref="ArgumentNullException">Thrown if project is null</exception>
    public void DeleteProject(Project project)
    {
        if (project == null) throw new ArgumentNullException(nameof(project));

        lock (_saveLock)
        {
            _projects.Remove(project);
            _pendingUpdateProjects.Add(project);
            
            _logger.Information("Deleted project {ProjectId}", project.Id);
        }
    }

    #endregion

    #region Space Operations

    /// <summary>
    /// Creates a new space within the specified project, optionally based on a template space.
    /// Sets the ID, creation/update metadata, and adds both the space and its parent project to the pending updates lists.
    /// </summary>
    /// <param name="user">The user creating the space</param>
    /// <param name="project">The parent project to add the space to</param>
    /// <param name="template">Optional template space to copy properties from</param>
    /// <returns>The newly created space</returns>
    /// <exception cref="ArgumentNullException">Thrown if project is null</exception>
    public Space CreateSpace(User user, Project project, Space template = null)
    {
        if (project == null) throw new ArgumentNullException(nameof(project));

        lock (_saveLock)
        {
            var space = new Space();
            if (template != null)
                space.AdoptState(template);

            space.Id = project.Spaces.Select(s => s.Id).DefaultIfEmpty(0).Max() + 1;
            project.AddSpace(space);
            
            space.CreationDate = DateTime.Now;
            space.CreatedBy = user;
            space.UpdateDate = DateTime.Now;
            space.UpdatedBy = user;
            
            _pendingUpdateSpaces.Add(space);
            _pendingUpdateProjects.Add(project);

            _logger.Information("Created space {SpaceId} in project {ProjectId} by user {UserId}", space.Id, project.Id, user.Id);
            return space;
        }
    }
    
    /// <summary>
    /// Reconnects an existing space from disk to a project.
    /// Loads the space file, resolves ID conflicts, and adds the space to the project.
    /// </summary>
    /// <param name="user">The user reconnecting the space</param>
    /// <param name="project">The project to reconnect the space to</param>
    /// <param name="spacePath">The path to the space file</param>
    /// <param name="users">Collection of users to resolve references</param>
    /// <returns>The reconnected space</returns>
    /// <exception cref="ArgumentNullException">Thrown if project is null</exception>
    /// <exception cref="Exception">Thrown if space file is invalid or cannot be loaded</exception>
    public Space ReconnectSpace(User user, Project project, string spacePath, ReadOnlyCollection<User> users)
    {
        if (project == null) throw new ArgumentNullException(nameof(project));
        if (users == null) throw new ArgumentNullException(nameof(users));
        if (string.IsNullOrEmpty(spacePath)) throw new ArgumentNullException(nameof(spacePath));

        lock (_saveLock)
        {
            // Verify the space file exists
            if (!File.Exists(spacePath))
                throw new Exception($"Space file not found at {spacePath}");
            
            // Get the directory containing the space file
            string rootDirectory = Path.GetDirectoryName(spacePath);
            if (string.IsNullOrEmpty(rootDirectory))
                throw new Exception("Invalid space path: cannot determine root directory");

            // Load the space from the file
            var spaceJson = JsonNode.Parse(File.ReadAllText(spacePath));
            if (spaceJson == null)
                throw new Exception("Failed to parse space file: invalid JSON");
            
            var space = new Space
            {
                Project = project
            };
            
            // Load the space data
            space.ReadFromJson(spaceJson, users);
            
            // If jobs were left active at disconnect time, mark all as failed
            foreach (var job in space.Jobs)
                if (job.Status.IsUnsettled())
                    job.Status = JobStatus.Failed;

            // Just in case the space got moved after it was last saved
            space.RootDirectory = rootDirectory;
            
            // Check for ID conflict within the project
            if (project.FindSpace(space.Id) != null)
            {
                // Assign a new ID
                space.Id = project.Spaces.Select(s => s.Id).DefaultIfEmpty(1).Max() + 1;
            }
            
            // Set metadata
            space.UpdateDate = DateTime.Now;
            space.UpdatedBy = user;
            
            // Add to project
            project.AddSpace(space);
            
            // Mark for saving
            _pendingUpdateSpaces.Add(space);
            _pendingUpdateProjects.Add(project);
            
            _logger.Information("Reconnected space {SpaceId} to project {ProjectId} by user {UserId}", space.Id, project.Id, user.Id);
            return space;
        }
    }

    /// <summary>
    /// Updates an existing space by applying the specified action to it.
    /// Updates the modification metadata and marks the space for auto-saving.
    /// </summary>
    /// <param name="user">The user performing the update</param>
    /// <param name="space">The space to update</param>
    /// <param name="updateAction">Action that performs the actual modifications to the space</param>
    /// <exception cref="ArgumentNullException">Thrown if space is null</exception>
    public void UpdateSpace(User user, Space space, Action<Space> updateAction)
    {
        if (space == null) throw new ArgumentNullException(nameof(space));

        lock (_saveLock)
        {
            if (updateAction != null)
                updateAction(space);
            space.UpdateDate = DateTime.Now;
            space.UpdatedBy = user;
            
            _pendingUpdateSpaces.Add(space);
            
            _logger.Debug("Updated space {SpaceId} in project {ProjectId} by user {UserId}", space.Id, space.Project.Id, user.Id);
        }
    }

    /// <summary>
    /// Removes a space from its parent project and marks both for updating during the next auto-save cycle.
    /// Note that this uses Project.DeleteSpace which also handles removing connected edges, jobs, etc.
    /// </summary>
    /// <param name="user">The user deleting the space</param>
    /// <param name="space">The space to delete</param>
    /// <exception cref="ArgumentNullException">Thrown if space is null</exception>
    public void DeleteSpace(User user, Space space)
    {
        if (space == null) throw new ArgumentNullException(nameof(space));

        lock (_saveLock)
        {
            _pendingUpdateProjects.Add(space.Project);
            
            space.Project.UpdateDate = DateTime.Now;
            space.Project.UpdatedBy = user;
            space.Project.DeleteSpace(space);
            
            _logger.Information("Deleted space {SpaceId} from project {ProjectId} by user {UserId}", space.Id, space.Project.Id, user.Id);
        }
    }

    #endregion

    #region Job Operations

    /// <summary>
    /// Creates a new job of the specified type within a space and adds it to the given view.
    /// Sets the creation/update metadata and marks the space for auto-saving.
    /// </summary>
    /// <param name="user">The user creating the job</param>
    /// <param name="space">The space to add the job to</param>
    /// <param name="view">The view where the job will be displayed</param>
    /// <param name="typeGuid">The type GUID of the job to create (must exist in Job.Types)</param>
    /// <param name="template">Optional template job to copy properties from</param>
    /// <returns>The newly created job</returns>
    /// <exception cref="ArgumentNullException">Thrown if space or view is null</exception>
    /// <exception cref="ArgumentException">Thrown if the specified job type does not exist</exception>
    public Job CreateJob(User user, Space space, View view, string typeGuid, Job template = null)
    {
        if (space == null) throw new ArgumentNullException(nameof(space));
        if (view == null) throw new ArgumentNullException(nameof(view));
        if (!Job.Types.ContainsKey(typeGuid))
            throw new ArgumentException($"Job type {typeGuid} does not exist");

        lock (_saveLock)
        {
            Job job = space.CreateJob(typeGuid, template, view);
            
            job.AddEvent(EventType.Created, user);
            job.UpdateDate = DateTime.Now;
            job.UpdatedBy = user;

            _pendingUpdateSpaces.Add(space);

            _logger.Information("Created job {JobId} of type {JobType} in space {SpaceId} by user {UserId}", job.Id, typeGuid, space.Id, user.Id);
            return job;
        }
    }

    /// <summary>
    /// Updates an existing job by applying the specified action to it.
    /// Updates the modification metadata and marks the job's space for auto-saving.
    /// </summary>
    /// <param name="user">The user performing the update</param>
    /// <param name="job">The job to update</param>
    /// <param name="updateAction">Action that performs the actual modifications to the job</param>
    /// <exception cref="ArgumentNullException">Thrown if job is null</exception>
    public void UpdateJob(User user, Job job, Action<Job> updateAction)
    {
        if (job == null) throw new ArgumentNullException(nameof(job));

        lock (_saveLock)
        {
            _pendingUpdateSpaces.Add(job.Space);
            
            if (updateAction != null)
                updateAction(job);
            job.UpdateDate = DateTime.Now;
            job.UpdatedBy = user;
            
            _logger.Debug("Updated job {JobId} in space {SpaceId} by user {UserId}", job.Id, job.Space.Id, user.Id);
        }
    }

    /// <summary>
    /// Removes a job from its space and marks the space for updating during the next auto-save cycle.
    /// Note that Space.DeleteJob also handles removing connected edges.
    /// </summary>
    /// <param name="user">The user deleting the job</param>
    /// <param name="job">The job to delete</param>
    /// <exception cref="ArgumentNullException">Thrown if job is null</exception>
    public void DeleteJob(User user, Job job)
    {
        if (job == null) throw new ArgumentNullException(nameof(job));

        lock (_saveLock)
        {
            _pendingUpdateSpaces.Add(job.Space);
            
            job.Space.DeleteJob(job);
            job.Space.UpdateDate = DateTime.Now;
            job.Space.UpdatedBy = user;
            
            _logger.Information("Deleted job {JobId} from space {SpaceId} by user {UserId}", job.Id, job.Space.Id, user.Id);
        }
    }

    /// <summary>
    /// Creates a copy of an existing job with a unique name, preserving all parameters and connections.
    /// The clone is added to the specified space and view, which may differ from the original.
    /// </summary>
    /// <param name="user">The user creating the clone</param>
    /// <param name="space">The space to add the clone to (may be different from original's space)</param>
    /// <param name="original">The job to clone</param>
    /// <param name="view">The view where the clone will be displayed</param>
    /// <returns>The newly created job clone</returns>
    /// <exception cref="ArgumentNullException">Thrown if space or view is null</exception>
    public Job CloneJob(User user, Space space, Job original, View view)
    {
        if (space == null) throw new ArgumentNullException(nameof(space));
        if (view == null) throw new ArgumentNullException(nameof(view));

        Job clone;

        lock (_saveLock)
        {
            clone = space.CreateJob(original.TypeGuid, original, view);
            clone.Status = JobStatus.Building;
            clone.DirectoryName = "";
            clone.Clear();

            // Find unique name for the clone
            int cloneId = 1;
            clone.Alias = string.IsNullOrWhiteSpace(original.Alias) ? 
                              $"Clone of J{original.Id}" : 
                              $"Clone of {original.Alias}";
            while (space.Jobs.Any(j => j != clone && j.Alias == clone.Alias))
            {
                cloneId++;
                clone.Alias = string.IsNullOrWhiteSpace(original.Alias) ?
                                  $"Clone {cloneId} of J{original.Id}" :
                                  $"Clone {cloneId} of {original.Alias}";
            }
            
            clone.Events.Clear();
            clone.AddEvent(EventType.Created, user);
            clone.UpdateDate = DateTime.Now;
            clone.UpdatedBy = user;

            _pendingUpdateSpaces.Add(space);
        }
            
        // Also copy all input port connections
        foreach (var port in original.PortsIn)
            foreach (var edge in port.Value.Edges)
                CreateEdge(space, edge.Source, clone.PortsIn[port.Key]);
        
        _logger.Information("Cloned job {OriginalJobId} to new job {CloneJobId} in space {SpaceId} by user {UserId}", original.Id, clone.Id, space.Id, user.Id);
        return clone;
    }

    #endregion
    
    #region Edge Operations

    /// <summary>
    /// Creates a new edge connecting an output port of one job to an input port of another job.
    /// Validates that both ports exist and are of the correct type before creating the connection.
    /// </summary>
    /// <param name="space">The space to add the edge to</param>
    /// <param name="from">The source port (must be an output port)</param>
    /// <param name="to">The target port (must be an input port)</param>
    /// <returns>The newly created edge</returns>
    /// <exception cref="ArgumentNullException">Thrown if space, from, or to is null</exception>
    /// <exception cref="Exception">Thrown if the source isn't an output port or target isn't an input port</exception>
    public Edge CreateEdge(Space space, Port from, Port to)
    {
        if (space == null) throw new ArgumentNullException(nameof(space));
        if (from == null) throw new ArgumentNullException(nameof(from));
        if (to == null) throw new ArgumentNullException(nameof(to));

        lock (_saveLock)
        {
            if (!from.Job.PortsOut.TryGetValue(from.Name, out PortOut fromPort))
                throw new Exception($"Source Job {from.Job.Id} doesn't have an output port named {from.Name}");

            if (!to.Job.PortsIn.TryGetValue(to.Name, out PortIn toPort))
                throw new Exception($"Target Job {to.Job.Id} doesn't have an input port named {to.Name}");

            var edge = space.CreateEdge(fromPort, toPort);
            _pendingUpdateSpaces.Add(space);

            _logger.Debug("Created edge {EdgeId} connecting job {SourceJobId} to job {TargetJobId} in space {SpaceId}", edge.Id, from.Job.Id, to.Job.Id, space.Id);
            return edge;
        }
    }

    /// <summary>
    /// Updates an existing edge by applying the specified action to it.
    /// Marks the edge's space for auto-saving.
    /// </summary>
    /// <param name="edge">The edge to update</param>
    /// <param name="updateAction">Action that performs the actual modifications to the edge</param>
    /// <exception cref="ArgumentNullException">Thrown if edge is null</exception>
    public void UpdateEdge(Edge edge, Action<Edge> updateAction)
    {
        if (edge == null) throw new ArgumentNullException(nameof(edge));

        lock (_saveLock)
        {
            _pendingUpdateSpaces.Add(edge.Space);
            
            if (updateAction != null)
                updateAction(edge);
        }
        
        _logger.Information("Updated edge {EdgeId} in space {SpaceId}", edge.Id, edge.Space.Id);
    }

    /// <summary>
    /// Removes an edge from its space and marks the space for updating during the next auto-save cycle.
    /// </summary>
    /// <param name="edge">The edge to delete</param>
    /// <exception cref="ArgumentNullException">Thrown if edge is null</exception>
    public void DeleteEdge(Edge edge)
    {
        if (edge == null) throw new ArgumentNullException(nameof(edge));

        lock (_saveLock)
        {
            _pendingUpdateSpaces.Add(edge.Space);
            
            edge.Space.DeleteEdge(edge);
            
            _logger.Debug("Deleted edge {EdgeId} in space {SpaceId}", edge.Id, edge.Space.Id);
        }
    }

    #endregion

    #region View Operations

    /// <summary>
    /// Creates a new view within the specified space, optionally based on a template view.
    /// Sets the creation/update metadata and marks the space for auto-saving.
    /// </summary>
    /// <param name="user">The user creating the view</param>
    /// <param name="space">The space to add the view to</param>
    /// <param name="template">Optional template view to copy properties from (default is a new empty view)</param>
    /// <returns>The newly created view</returns>
    /// <exception cref="ArgumentNullException">Thrown if space is null</exception>
    public View CreateView(User user, Space space, View template = null)
    {
        if (space == null) throw new ArgumentNullException(nameof(space));

        lock (_saveLock)
        {
            View view = space.CreateView(template ?? new View());
            
            view.CreationDate = DateTime.Now;
            view.CreatedBy = user;
            view.UpdateDate = DateTime.Now;
            view.UpdatedBy = user;

            _pendingUpdateSpaces.Add(space);

            _logger.Information("Created view {ViewId} in space {SpaceId} by user {UserId}", view.Id, space.Id, user.Id);
            return view;
        }
    }

    /// <summary>
    /// Updates an existing view by applying the specified action to it.
    /// Updates the modification metadata and marks the view's space for auto-saving.
    /// </summary>
    /// <param name="user">The user performing the update</param>
    /// <param name="view">The view to update</param>
    /// <param name="updateAction">Action that performs the actual modifications to the view</param>
    /// <exception cref="ArgumentNullException">Thrown if view is null</exception>
    public void UpdateView(User user, View view, Action<View> updateAction)
    {
        if (view == null) throw new ArgumentNullException(nameof(view));

        lock (_saveLock)
        {
            if (updateAction != null)
                updateAction(view);
            view.UpdateDate = DateTime.Now;
            view.UpdatedBy = user;
            
            _pendingUpdateSpaces.Add(view.Space);
            
            _logger.Debug("Updated view {ViewId} in space {SpaceId} by user {UserId}", view.Id, view.Space.Id, user.Id);
        }
    }

    /// <summary>
    /// Removes a view from its space and marks the space for updating during the next auto-save cycle.
    /// </summary>
    /// <param name="user">The user deleting the view</param>
    /// <param name="view">The view to delete</param>
    /// <exception cref="ArgumentNullException">Thrown if view is null</exception>
    public void DeleteView(User user, View view)
    {
        if (view == null) throw new ArgumentNullException(nameof(view));

        lock (_saveLock)
        {
            _pendingUpdateSpaces.Add(view.Space);
            
            view.Space.DeleteView(view);
            view.Space.UpdateDate = DateTime.Now;
            view.Space.UpdatedBy = user;
            
            _logger.Information("Deleted view {ViewId} from space {SpaceId} by user {UserId}", view.Id, view.Space.Id, user.Id);
        }
    }

    /// <summary>
    /// Adds a job to a view, making it visible in that view.
    /// Updates the modification metadata and marks the view's space for auto-saving.
    /// </summary>
    /// <param name="user">The user adding the job</param>
    /// <param name="view">The view to add the job to</param>
    /// <param name="job">The job to add to the view</param>
    /// <exception cref="ArgumentNullException">Thrown if view or job is null</exception>
    public void AddJobToView(User user, View view, Job job)
    {
        if (view == null) throw new ArgumentNullException(nameof(view));
        if (job == null) throw new ArgumentNullException(nameof(job));

        lock (_saveLock)
        {
            view.UpdateDate = DateTime.Now;
            view.UpdatedBy = user;
            view.AddJob(job);
            
            _pendingUpdateSpaces.Add(view.Space);
            
            _logger.Information("Added job {JobId} to view {ViewId} by user {UserId}", job.Id, view.Id, user.Id);
        }
    }

    /// <summary>
    /// Removes a job from a view, making it invisible in that view.
    /// Note that this does not delete the job from the space - the job continues to exist and function.
    /// </summary>
    /// <param name="user">The user removing the job</param>
    /// <param name="view">The view to remove the job from</param>
    /// <param name="job">The job to remove from the view</param>
    /// <exception cref="ArgumentNullException">Thrown if view or job is null</exception>
    public void RemoveJobFromView(User user, View view, Job job)
    {
        if (view == null) throw new ArgumentNullException(nameof(view));
        if (job == null) throw new ArgumentNullException(nameof(job));

        lock (_saveLock)
        {
            view.UpdateDate = DateTime.Now;
            view.UpdatedBy = user;
            view.RemoveJob(job);
            
            _pendingUpdateSpaces.Add(view.Space);
            
            _logger.Information("Removed job {JobId} from view {ViewId} by user {UserId}", job.Id, view.Id, user.Id);
        }
    }

    /// <summary>
    /// Marks a space for saving in the next auto-save cycle.
    /// Used by DataManager for operations that modify view/folder state directly.
    /// </summary>
    public void MarkSpaceForSave(Space space)
    {
        if (space == null) return;
        lock (_saveLock)
        {
            _pendingUpdateSpaces.Add(space);
        }
    }

    #endregion

    #region Find objects

    /// <summary>
    /// Finds a project by its ID.
    /// </summary>
    /// <param name="projectId">The ID of the project to find</param>
    /// <returns>The project with the specified ID, or null if not found</returns>
    public Project FindProject(int projectId)
    {
        Project p = _projects.FirstOrDefault(p => p.Id == projectId);
        if (p == null)
        {
            _logger.Warning("Project with ID {ProjectId} not found", projectId);
            return null;
        }

        return p;
    }

    /// <summary>
    /// Finds a space by its ID and the ID of its parent project.
    /// </summary>
    /// <param name="projectId">The ID of the parent project</param>
    /// <param name="spaceId">The ID of the space to find</param>
    /// <returns>The space with the specified ID, or null if not found</returns>
    public Space FindSpace(long projectId, long spaceId)
    {
        Project p = _projects.FirstOrDefault(p => p.Id == projectId);
        if (p == null)
        {
            _logger.Warning("Project with ID {ProjectId} not found", projectId);
            return null;
        }

        Space s = p.Spaces.FirstOrDefault(g => g.Id == spaceId);
        if (s == null)
        {
            _logger.Warning("Space with ID {SpaceId} not found in project {ProjectId}", spaceId, projectId);
            return null;
        }

        return s;
    }

    /// <summary>
    /// Finds a job by its ID, the ID of its parent space, and the ID of its parent project.
    /// </summary>
    /// <param name="projectId">The ID of the parent project</param>
    /// <param name="spaceId">The ID of the parent space</param>
    /// <param name="jobId">The ID of the job to find</param>
    /// <returns>The job with the specified ID, or null if not found</returns>
    public Job FindJob(long projectId, long spaceId, long jobId)
    {
        Project p = _projects.FirstOrDefault(p => p.Id == projectId);
        if (p == null)
        {
            _logger.Warning("Project with ID {ProjectId} not found", projectId);
            return null;
        }

        Space s = p.Spaces.FirstOrDefault(g => g.Id == spaceId);
        if (s == null)
        {
            _logger.Warning("Space with ID {SpaceId} not found in project {ProjectId}", spaceId, projectId);
            return null;
        }

        Job j = s.Jobs.FirstOrDefault(n => n.Id == jobId);
        if (j == null)
        {
            _logger.Warning("Job with ID {JobId} not found in project {ProjectId}, space {SpaceId}", jobId, projectId, spaceId);
            return null;
        }

        return j;
    }

    /// <summary>
    /// Finds an edge by its ID, the ID of its parent space, and the ID of its parent project.
    /// </summary>
    /// <param name="projectId">The ID of the parent project</param>
    /// <param name="spaceId">The ID of the parent space</param>
    /// <param name="edgeId">The ID of the edge to find</param>
    /// <returns>The edge with the specified ID, or null if not found</returns>
    public Edge FindEdge(long projectId, long spaceId, long edgeId)
    {
        Project p = _projects.FirstOrDefault(p => p.Id == projectId);
        if (p == null)
        {
            _logger.Warning("Project with ID {ProjectId} not found", projectId);
            return null;
        }

        Space s = p.Spaces.FirstOrDefault(g => g.Id == spaceId);
        if (s == null)
        {
            _logger.Warning("Space with ID {SpaceId} not found in project {ProjectId}", spaceId, projectId);
            return null;
        }

        Edge e = s.Edges.FirstOrDefault(e => e.Id == edgeId);
        if (e == null)
        {
            _logger.Warning("Edge with ID {EdgeId} not found in project {ProjectId}, space {SpaceId}", edgeId, projectId, spaceId);
            return null;
        }

        return e;
    }

    /// <summary>
    /// Finds a view by its ID, the ID of its parent space, and the ID of its parent project.
    /// </summary>
    /// <param name="projectId">The ID of the parent project</param>
    /// <param name="spaceId">The ID of the parent space</param>
    /// <param name="viewId">The ID of the view to find</param>
    /// <returns>The view with the specified ID, or null if not found</returns>
    public View FindView(long projectId, long spaceId, long viewId)
    {
        Project p = _projects.FirstOrDefault(p => p.Id == projectId);
        if (p == null)
        {
            _logger.Warning("Project with ID {ProjectId} not found", projectId);
            return null;
        }

        Space s = p.Spaces.FirstOrDefault(g => g.Id == spaceId);
        if (s == null)
        {
            _logger.Warning("Space with ID {SpaceId} not found in project {ProjectId}", spaceId, projectId);
            return null;
        }

        View v = s.Views.FirstOrDefault(v => v.Id == viewId);
        if (v == null)
        {
            _logger.Warning("View with ID {ViewId} not found in project {ProjectId}, space {SpaceId}", viewId, projectId, spaceId);
            return null;
        }

        return v;
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes of the resources used by the DataRepository.
    /// Saves any pending changes and releases the auto-save timer.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases unmanaged and - optionally - managed resources.
    /// </summary>
    /// <param name="disposing">True to release both managed and unmanaged resources; false to release only unmanaged resources</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            SaveChanges(null); // Final save to persist any pending changes
            _autoSaveTimer.Dispose();
        }

        _disposed = true;
    }

    #endregion
}
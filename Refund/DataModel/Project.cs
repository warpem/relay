using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Refund.DataModel.ReadOnly;
using Serilog;

namespace Refund.DataModel;

/// <summary>
/// Represents a top-level container for spaces and workflows.
/// Projects organize related spaces and manage user access permissions.
/// They provide a way to group workflows that belong to the same research project or experiment.
/// </summary>
public class Project : RelayBase
{
    /// <summary>
    /// Cache of read-only wrappers for projects, using weak references to avoid memory leaks.
    /// </summary>
    private static readonly ConditionalWeakTable<Project, ReadOnlyProject> ReadOnlyCache = new();
        
    /// <summary>
    /// Unique identifier for this project.
    /// </summary>
    [RelayProperty]
    public int Id { get; set; } = -1;

    /// <summary>
    /// User-defined name for the project.
    /// This provides a human-readable identifier displayed in the UI.
    /// </summary>
    [RelayProperty]
    public string Alias { get; set; } = string.Empty;
    
    /// <summary>
    /// Date and time when this project was created.
    /// </summary>
    [RelayProperty]
    public DateTime CreationDate { get; set; }
    
    /// <summary>
    /// User who created this project.
    /// </summary>
    public User CreatedBy { get; set; }
    
    /// <summary>
    /// Date and time when this project was last updated.
    /// </summary>
    [RelayProperty]
    public DateTime UpdateDate { get; set; }
    
    /// <summary>
    /// User who last updated this project.
    /// </summary>
    public User UpdatedBy { get; set; }

    /// <summary>
    /// Path to the hero image for this project.
    /// The hero image is displayed in the UI as a banner or icon for the project.
    /// </summary>
    [RelayProperty]
    public string HeroImage { get; set; } = string.Empty;

    /// <summary>
    /// User-provided notes or description of this project.
    /// </summary>
    [RelayProperty]
    public string Notes { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets a fully qualified name for the project, including its ID and alias.
    /// This is used in places where a unique, human-readable identifier is needed.
    /// </summary>
    public string QualifiedName => $"P{Id}: {Alias}";
    
    /// <summary>
    /// The user who owns this project.
    /// The owner has full control over the project and can add or remove members.
    /// </summary>
    public User Owner { get; set; }

    /// <summary>
    /// Internal list of users who are members of this project.
    /// </summary>
    private readonly List<User> _members = new();
    
    /// <summary>
    /// Read-only collection of users who are members of this project.
    /// Project members have access to the spaces and jobs within the project.
    /// </summary>
    public ReadOnlyCollection<User> Members => _members.AsReadOnly();

    /// <summary>
    /// List of file paths to the space files in this project.
    /// These paths are used to load spaces when the project is opened.
    /// </summary>
    private List<string> SpacePaths { get; } = new();

    /// <summary>
    /// Internal list of spaces in this project.
    /// </summary>
    private readonly List<Space> _spaces = new();
    
    /// <summary>
    /// Read-only collection of spaces in this project.
    /// Spaces are containers for workflows of connected jobs.
    /// </summary>
    public ReadOnlyCollection<Space> Spaces => _spaces.AsReadOnly();
    
    /// <summary>
    /// Returns a read-only wrapper for this project.
    /// The read-only wrapper provides a safe view that prevents accidental modification.
    /// The same wrapper instance is reused for each project to minimize object creation.
    /// </summary>
    /// <returns>A read-only wrapper for this project</returns>
    public ReadOnlyProject AsReadOnly()
    {
        return ReadOnlyCache.GetValue(this, project => new ReadOnlyProject(project));
    }

    /// <summary>
    /// Creates a new space in this project.
    /// If a template is provided, the new space will adopt its state.
    /// </summary>
    /// <param name="template">Optional template space to copy settings from</param>
    /// <returns>The newly created space</returns>
    /// <exception cref="Exception">Thrown if the space directory already exists</exception>
    public Space CreateSpace(Space template)
    {
        var space = new Space();

        if (template != null)
            space.AdoptState(template);
        
        if (File.Exists(space.FilePath))
            throw new Exception($"Directory {space.RootDirectory} appears to already contain a space. Please choose a different location.");

        space.Id = _spaces.Select(s => s.Id).DefaultIfEmpty(-1).Max() + 1;

        AddSpace(space);

        return space;
    }

    /// <summary>
    /// Adds an existing space to this project.
    /// Sets the space's Project property to this project and adds its path to SpacePaths.
    /// </summary>
    /// <param name="space">The space to add</param>
    public void AddSpace(Space space)
    {
        int insertPosition = 0;
        while (insertPosition < _spaces.Count && _spaces[insertPosition].Id <= space.Id)
            insertPosition++;
        
        _spaces.Insert(insertPosition, space);
        space.Project = this;

        if (!SpacePaths.Contains(space.FilePath))
            SpacePaths.Insert(insertPosition, space.FilePath);
    }

    /// <summary>
    /// Deletes a space from this project.
    /// Removes the space from the spaces collection and its path from SpacePaths.
    /// </summary>
    /// <param name="space">The space to delete</param>
    public void DeleteSpace(Space space)
    {
        _spaces.Remove(space);

        SpacePaths.Remove(space.FilePath);
    }

    /// <summary>
    /// Moves a space from one location to another.
    /// Updates the space's root directory and adjusts its path in SpacePaths.
    /// </summary>
    /// <param name="space">The space to move</param>
    /// <param name="from">The original location</param>
    /// <param name="to">The new location</param>
    public void MoveSpace(Space space, string from, string to)
    {
        space.RootDirectory = to;
        SpacePaths.Remove(Path.Combine(from, "space.relay"));
        SpacePaths.Add(Path.Combine(to, "space.relay"));
    }

    /// <summary>
    /// Finds a space in this project by its ID.
    /// </summary>
    /// <param name="id">The ID of the space to find</param>
    /// <returns>The space with the specified ID, or null if not found</returns>
    public Space FindSpace(int id) => _spaces.FirstOrDefault(s => s.Id == id);
    
    /// <summary>
    /// Adds a user as a member of this project.
    /// Project members have access to view and edit spaces within the project.
    /// </summary>
    /// <param name="user">The user to add as a member</param>
    public void AddMember(User user) => _members.Add(user);

    /// <summary>
    /// Removes a user from the members of this project.
    /// This revokes the user's access to the project.
    /// </summary>
    /// <param name="user">The user to remove</param>
    public void RemoveMember(User user) => _members.Remove(user);

    /// <summary>
    /// Loads all spaces in this project from their files.
    /// Each space is read from its file path stored in SpacePaths.
    /// </summary>
    /// <param name="users">Collection of users to resolve references to owners and members</param>
    public void LoadSpaces(ReadOnlyCollection<User> users)
    {
        _spaces.Clear();

        foreach (var path in SpacePaths.ToArray())
        {
            if (!File.Exists(path))
            {
                Log.ForContext<Project>().Warning("Space file not found, removing from project {ProjectId}: {SpacePath}", Id, path);
                SpacePaths.Remove(path);
                continue;
            }

            var space = new Space { Project = this };
            var spaceJson = JsonNode.Parse(File.ReadAllText(path));
            space.ReadFromJson(spaceJson, users);

            if (space.Id >= 0)
                AddSpace(space);
            else
                Log.ForContext<Project>().Error("Couldn't load Space from path {SpacePath} in project {ProjectId}", path, Id);
        }
    }

    /// <summary>
    /// Serializes this project to a JSON node.
    /// This saves the project's properties, owner, members, and space paths.
    /// </summary>
    /// <param name="writer">The JSON node to write to</param>
    public override void WriteToJson(JsonNode writer)
    {
        base.WriteToJson(writer);

        writer["CreatedBy"] = CreatedBy?.Id;
        writer["UpdatedBy"] = UpdatedBy?.Id;

        writer["Owner"] = Owner?.Id;
        writer["Members"] = new JsonArray(Members.Select(m => JsonValue.Create(m.Id)).ToArray<JsonNode>());
        
        writer["Spaces"] = new JsonArray(SpacePaths.Select(p => JsonValue.Create(p)).ToArray<JsonNode>());
    }

    /// <summary>
    /// Deserializes this project from a JSON node, resolving references to users.
    /// This loads the project's properties, owner, members, and space paths.
    /// </summary>
    /// <param name="reader">The JSON node to read from</param>
    /// <param name="users">Collection of users to resolve references from</param>
    /// <exception cref="Exception">Thrown if owner and members cannot be resolved</exception>
    public void ReadFromJson(JsonNode reader, ReadOnlyCollection<User> users)
    {
        ReadFromJson(reader);

        if (reader["Owner"] != null)
            Owner = users.FirstOrDefault(u => u.Id == reader["Owner"].Deserialize<int>());

        _members.Clear();
        if (reader["Members"] != null)
            foreach (var m in reader["Members"].AsArray())
                _members.Add(users.FirstOrDefault(u => u.Id == m.Deserialize<int>()));
        _members.RemoveAll(m => m == null);

        // Fall back to reasonable defaults if data is missing
        if (Owner == null)
            Owner = _members.FirstOrDefault();
        if (Owner == null)
            Owner = users.FirstOrDefault();
        if (Owner == null)
            throw new Exception($"{QualifiedName} doesn't have owner or members.");
        
        if (reader["CreatedBy"] != null)
            CreatedBy = users.FirstOrDefault(u => u.Id == reader["CreatedBy"].Deserialize<int>());
        
        if (CreatedBy == null)
            CreatedBy = Owner;
        if (CreatedBy == null)
            CreatedBy = Members.FirstOrDefault();
        if (CreatedBy == null)
            CreatedBy = users.FirstOrDefault();

        if (reader["UpdatedBy"] != null)
            UpdatedBy = users.FirstOrDefault(u => u.Id == reader["UpdatedBy"].Deserialize<int>());
        
        if (UpdatedBy == null)
            UpdatedBy = Owner;
        if (UpdatedBy == null)
            UpdatedBy = Members.FirstOrDefault();
        if (UpdatedBy == null)
            UpdatedBy = users.FirstOrDefault();
    }

    /// <summary>
    /// Overridden implementation of ReadFromJson that handles base deserialization and space paths.
    /// </summary>
    /// <param name="reader">The JSON node to read from</param>
    public override void ReadFromJson(JsonNode reader)
    {
        base.ReadFromJson(reader);

        SpacePaths.Clear();
        reader["Spaces"].Deserialize<List<string>>().ForEach(s => SpacePaths.Add(s));
    }

    /// <summary>
    /// Creates a shallow copy of this project.
    /// The clone will have the same properties but not the same references to spaces.
    /// </summary>
    /// <returns>A shallow copy of this project</returns>
    public Project Clone()
    {
        var clone = new Project();
        clone.ReadFromJson(ToJson());

        return clone;
    }
}
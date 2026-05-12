using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Refund.DataModel;
using Refund.Services.Core.Session;
using Relay.Emoji;

namespace Relay.Screens.Main.Project;

/// <summary>
/// Model class for creating a new space or reconnecting an existing space,
/// containing all properties required for space creation and validation attributes
/// for form validation. Used by CreateSpaceDialog to collect and validate user input.
/// </summary>
public class CreateSpaceModel
{
    /// <summary>
    /// The emoji or icon representing the space in the UI. 
    /// Defaults to a random emoji from the EmojiLibrary.
    /// </summary>
    public string HeroImage { get; set; } = EmojiLibrary.GetRandom().Glyph;
    
    /// <summary>
    /// The display name of the space within the project.
    /// Must be unique within the project, between 3-150 characters,
    /// and can only contain letters, numbers, spaces, hyphens and underscores.
    /// </summary>
    [Required(ErrorMessage = "Space name is required")]
    [MinLength(3, ErrorMessage = "Space name must be at least 3 characters long")]
    [MaxLength(150, ErrorMessage = "Space name cannot be longer than 150 characters")]
    [RegularExpression(@"^[a-zA-Z0-9\s\-_]+$", 
                       ErrorMessage = "Space name can only contain letters, numbers, spaces, hyphens and underscores")]
    [UniqueSpaceName]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional description or notes about the space.
    /// </summary>
    public string Notes { get; set; } = string.Empty;
    
    /// <summary>
    /// The file system directory where the space's data will be stored.
    /// Must exist, be writable. If it contains a Relay space that's not connected
    /// to any project, it can be reconnected.
    /// </summary>
    [Required(ErrorMessage = "Space directory is required")]
    [DirectoryValid]
    public string Directory { get; set; } = string.Empty;
    
    /// <summary>
    /// Indicates if the current directory contains an existing space file
    /// that can be reconnected to the current project.
    /// </summary>
    public bool IsReconnectMode { get; set; } = false;
    
    /// <summary>
    /// Indicates if the current directory contains an existing space file
    /// that is already connected to a project.
    /// </summary>
    public bool IsAlreadyConnected { get; set; } = false;
    
    /// <summary>
    /// The ID of the existing space, if reconnecting.
    /// </summary>
    public int ExistingSpaceId { get; set; } = -1;
    
    /// <summary>
    /// The path to the existing space file, if reconnecting.
    /// </summary>
    public string ExistingSpacePath { get; set; } = string.Empty;
}

/// <summary>
/// Custom validation attribute that ensures the space name is unique within the current project.
/// Uses the RelaySession service to check against existing space names.
/// </summary>
public class UniqueSpaceNameAttribute : ValidationAttribute
{
    /// <summary>
    /// Validates that the space name is unique within the current project.
    /// </summary>
    /// <param name="value">The space name to validate</param>
    /// <param name="context">The validation context, used to access the RelaySession</param>
    /// <returns>ValidationResult.Success if valid, or an error message if invalid</returns>
    protected override ValidationResult IsValid(object value, ValidationContext context)
    {
        var session = context.GetService<RelaySession>();
        
        if (value is string spaceName &&
            session.Project?.Spaces.Any(s => s.Alias.Equals(spaceName.Trim(), StringComparison.OrdinalIgnoreCase)) == true)
        {
            return new ValidationResult("A space with this name already exists in this project");
        }
        
        return ValidationResult.Success;
    }
}

/// <summary>
/// Custom validation attribute that ensures the directory is valid for a new space
/// or contains a space that can be reconnected.
/// Checks that the directory exists, is writable, and handles existing space files.
/// </summary>
public class DirectoryValidAttribute : ValidationAttribute
{
    /// <summary>
    /// Validates that the directory exists, is writable, and handles existing space files.
    /// </summary>
    /// <param name="value">The directory path to validate</param>
    /// <param name="context">The validation context</param>
    /// <returns>ValidationResult.Success if valid, or an error message if invalid</returns>
    protected override ValidationResult IsValid(object value, ValidationContext context)
    {
        var model = context.ObjectInstance as CreateSpaceModel;
        if (model == null) return new ValidationResult("Invalid model type");
        
        if (value is string spaceDir)
        {
            if (!Directory.Exists(spaceDir))
            {
                return new ValidationResult("This directory does not exist");
            }

            try
            {
                // Test write permissions by creating and deleting a temporary file
                var tempFile = Path.GetTempFileName();
                tempFile = Path.Combine(spaceDir, Path.GetFileName(tempFile));

                using (var file = File.Create(tempFile))
                {
                    file.WriteByte(0);
                }

                File.Delete(tempFile);
            }
            catch (Exception e)
            {
                return new ValidationResult("Relay doesn't have write permissions in this directory");
            }
            
            // Check if a Relay space already exists in this directory
            string spaceFilePath = Path.Combine(spaceDir, "space.relay");
            if (File.Exists(spaceFilePath))
            {
                // Space file exists - check if it belongs to any project
                var session = context.GetService<RelaySession>();
                var dataManager = context.GetService<Refund.Services.Core.DataManager.DataManager>();
                
                bool isConnected = IsSpaceConnectedToAnyProject(spaceFilePath, dataManager);
                if (isConnected)
                {
                    // Found a space that's already connected to a project
                    model.IsReconnectMode = false;
                    model.IsAlreadyConnected = true;
                    model.ExistingSpacePath = string.Empty;
                    model.ExistingSpaceId = -1;
                    
                    return new ValidationResult("A Relay space already exists in this directory and is connected to a project");
                }
                else
                {
                    // Found a disconnected space - set reconnect mode
                    model.IsReconnectMode = true;
                    model.IsAlreadyConnected = false;
                    model.ExistingSpacePath = spaceFilePath;
                    
                    // Load basic information from the space file
                    try
                    {
                        LoadExistingSpaceInfo(model, spaceFilePath);
                    }
                    catch (Exception ex)
                    {
                        return new ValidationResult($"Found a space file but it appears to be invalid: {ex.Message}");
                    }
                }
            }
            else
            {
                // Reset both flags if directory changes
                model.IsReconnectMode = false;
                model.IsAlreadyConnected = false;
                model.ExistingSpacePath = string.Empty;
                model.ExistingSpaceId = -1;
            }
        }

        return ValidationResult.Success;
    }
    
    /// <summary>
    /// Loads basic information from an existing space file
    /// </summary>
    private void LoadExistingSpaceInfo(CreateSpaceModel model, string spaceFilePath)
    {
        if (File.Exists(spaceFilePath))
        {
            var spaceJson = JsonNode.Parse(File.ReadAllText(spaceFilePath));
            if (spaceJson != null)
            {
                // Extract basic properties
                model.Name = spaceJson["Alias"]?.ToString() ?? string.Empty;
                model.Notes = spaceJson["Notes"]?.ToString() ?? string.Empty;
                model.HeroImage = spaceJson["HeroImage"]?.ToString() ?? "🪐";
                
                // Extract ID
                if (spaceJson["Id"] != null)
                {
                    model.ExistingSpaceId = spaceJson["Id"].GetValue<int>();
                }
            }
        }
    }
    
    /// <summary>
    /// Checks if a space file is already connected to any loaded project
    /// </summary>
    private bool IsSpaceConnectedToAnyProject(string spaceFilePath, Refund.Services.Core.DataManager.DataManager dataManager)
    {
        // Check if the space is already connected to any project
        foreach (var project in dataManager.Projects)
        {
            foreach (var space in project.Spaces)
            {
                if (Path.GetFullPath(Path.Combine(space.RootDirectory, "space.relay")) == 
                    Path.GetFullPath(spaceFilePath))
                {
                    return true;
                }
            }
        }
        
        return false;
    }
}
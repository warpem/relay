using System.ComponentModel.DataAnnotations;
using Refund.Services.Core.DataManager;
using Relay.Emoji;

namespace Relay.Screens.Main.Home;

/// <summary>
/// Model for collecting and validating project creation data.
/// Used by the CreateProjectDialog to capture user input when creating a new project.
/// </summary>
public class CreateProjectModel
{
    /// <summary>
    /// The emoji glyph that represents the project visually.
    /// Defaults to a random emoji from the EmojiLibrary.
    /// Used as the project's HeroImage when creating a new Project entity.
    /// </summary>
    public string HeroImage { get; set; } = EmojiLibrary.GetRandom().Glyph;
    
    /// <summary>
    /// The name of the project.
    /// Must be unique across all projects, at least 3 characters, and no more than 150 characters.
    /// Used as the project's Alias when creating a new Project entity.
    /// </summary>
    [Required(ErrorMessage = "Project name is required")]
    [MinLength(3, ErrorMessage = "Project name must be at least 3 characters long")]
    [MaxLength(150, ErrorMessage = "Project name cannot be longer than 150 characters")]
    [UniqueProjectName]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional description or notes about the project.
    /// Used as the project's Notes field when creating a new Project entity.
    /// </summary>
    public string Notes { get; set; } = string.Empty;
}

/// <summary>
/// Custom validation attribute that ensures project names are unique across the application.
/// Integrates with DataManager to check existing project names.
/// </summary>
public class UniqueProjectNameAttribute : ValidationAttribute
{
    /// <summary>
    /// Validates that the given project name doesn't already exist in the system.
    /// </summary>
    /// <param name="value">The project name to validate</param>
    /// <param name="context">Validation context containing services</param>
    /// <returns>Success if the name is unique, error result otherwise</returns>
    protected override ValidationResult IsValid(object value, ValidationContext context)
    {
        var dataManager = context.GetService<DataManager>();
        
        if (value is string projectName &&
            dataManager.Projects.Any(p => p.Alias.Equals(projectName.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return new ValidationResult("A project with this name already exists");
        }
        
        return ValidationResult.Success;
    }
}
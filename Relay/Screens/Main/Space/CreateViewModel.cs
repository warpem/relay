using System.ComponentModel.DataAnnotations;
using Refund.Services.Core.Session;
using Relay.Emoji;

namespace Relay.Screens.Main.Space;

/// <summary>
/// View model for the create view dialog form.
/// 
/// Contains properties for the new view with validation attributes
/// to ensure data integrity before submission. Used exclusively by the CreateViewDialog
/// to capture and validate user input when creating a new view. The model's properties
/// map directly to the Refund.DataModel.View entity's properties when creating a new view
/// through the DataManager.CreateView method.
/// </summary>
public class CreateViewModel
{
    /// <summary>
    /// Emoji or image representation for the view.
    /// Initialized with a random emoji from the EmojiLibrary to provide
    /// a default visual identifier. This property is directly mapped to
    /// the View.HeroImage property when creating a new view.
    /// </summary>
    public string HeroImage { get; set; } = EmojiLibrary.GetRandom().Glyph;

    /// <summary>
    /// Name for the new view with comprehensive validation.
    /// 
    /// The name must:
    /// - Be provided (required)
    /// - Be between 3-150 characters
    /// - Contain only alphanumeric characters, spaces, hyphens and underscores
    /// - Be unique within the current space (enforced by UniqueViewNameAttribute)
    /// 
    /// This property maps to the View.Alias property during view creation
    /// and serves as the primary identifier displayed to users.
    /// </summary>
    [Required(ErrorMessage = "View name is required")]
    [MinLength(3, ErrorMessage = "View name must be at least 3 characters long")]
    [MaxLength(150, ErrorMessage = "View name cannot be longer than 150 characters")]
    [RegularExpression(@"^[a-zA-Z0-9\s\-_]+$", 
                       ErrorMessage = "View name can only contain letters, numbers, spaces, hyphens and underscores")]
    [UniqueViewName]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional descriptive text for the view.
    /// Allows users to document the purpose or content of the view.
    /// Maps directly to the View.Notes property during view creation.
    /// </summary>
    public string Notes { get; set; } = string.Empty;
}

/// <summary>
/// Custom validation attribute that ensures view names are unique within a space.
/// 
/// Uses the current RelaySession to access the space's existing views and
/// performs a case-insensitive comparison to prevent duplicate view names.
/// This validation happens on the client side before submission to provide
/// immediate feedback to users.
/// 
/// Applied to the Name property in CreateViewModel to prevent duplicate view
/// names in the UI form validation phase, complementing server-side uniqueness
/// constraints.
/// </summary>
public class UniqueViewNameAttribute : ValidationAttribute
{
    /// <summary>
    /// Validates that the view name doesn't already exist in the current space.
    /// 
    /// Accesses the RelaySession from the validation context and checks
    /// if any existing view in the current space has the same name
    /// (case-insensitive comparison). The trim operation ensures that
    /// leading/trailing whitespace doesn't cause false uniqueness.
    /// 
    /// Used during form validation in CreateViewDialog before attempting
    /// to create the view via DataManager.
    /// </summary>
    /// <param name="value">The view name to validate</param>
    /// <param name="context">Validation context providing access to services</param>
    /// <returns>Success if the name is unique, error result otherwise</returns>
    protected override ValidationResult IsValid(object value, ValidationContext context)
    {
        var session = context.GetService<RelaySession>();
        
        if (value is string viewName &&
            session.Space.Views.Any(p => p.Alias.Equals(viewName.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return new ValidationResult("A view with this name already exists");
        }
        
        return ValidationResult.Success;
    }
}
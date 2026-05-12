namespace Refund.UIFields;

/// <summary>
/// Field attribute for emoji selection. Renders as an emoji picker in the UI.
/// Used to provide visual indicators or categorization for jobs, making it easier to
/// identify job types or states at a glance in the workflow graph.
/// 
/// Primarily used in documentation jobs like the "Vibe" job type, which allows researchers
/// to annotate their emotional response or sentiment at specific points in the cryo-EM
/// processing workflow. This creates an emotional timeline that can be referenced during
/// project review or when sharing workflows with collaborators.
/// </summary>
public class UiEmoji : UiFieldBase
{
    /// <summary>
    /// Gets the Blazor component type used to render this field (UiEmojiView)
    /// </summary>
    public override Type ViewType => typeof(UiEmojiView);

    /// <summary>
    /// Creates a new emoji selection field with the specified properties
    /// </summary>
    /// <param name="cliName">Command-line argument name (typically empty as in the Vibe job)</param>
    /// <param name="label">Display label in the UI (e.g., "Vibe" in emotional annotation jobs)</param>
    /// <param name="helpText">Optional tooltip text explaining the purpose of the emoji selection</param>
    /// <param name="isAdvanced">Whether this is an advanced option (typically false for emoji selectors)</param>
    public UiEmoji(string cliName, string label, string helpText = "", bool isAdvanced = false)
        : base(cliName, label, helpText, isAdvanced) { }
}
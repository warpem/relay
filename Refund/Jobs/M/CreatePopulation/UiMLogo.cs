using Refund.UIFields;

namespace Refund.Jobs.M.CreatePopulation;

public class UiMLogo : UiFieldBase
{
    /// <summary>
    /// Gets the Blazor component type used to render this field (UiBoolView)
    /// </summary>
    public override Type ViewType => typeof(UiMLogoView);

    /// <summary>
    /// Creates a new boolean field with the specified properties
    /// </summary>
    /// <param name="cliName">Command-line argument name used when generating job execution commands</param>
    /// <param name="label">Display label in the UI</param>
    /// <param name="helpText">Optional tooltip text that explains the parameter's purpose and impact</param>
    /// <param name="isAdvanced">Whether this is an advanced option hidden by default in the UI</param>
    /// <param name="reverse">Whether to reverse the logical meaning (true = off, false = on)</param>
    public UiMLogo(string label, string helpText ="", bool isAdvanced = false) 
        : base("", label, helpText, isAdvanced)
    {
    }
}
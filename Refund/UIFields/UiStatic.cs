namespace Refund.UIFields;

public class UiStatic : UiFieldBase
{
    /// <summary>
    /// Gets the Blazor component type used to render this field (UiStaticView)
    /// </summary>
    public override Type ViewType => typeof(UiStaticView);

    public UiStatic(string cliName, string label, string helpText = "", bool isAdvanced = false)
        : base(cliName, label, helpText, isAdvanced)
    {
        
    }
}
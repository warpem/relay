namespace Refund.UIFields;

public class UiMSpecies : UiFieldBase
{
    /// <summary>
    /// Gets the Blazor component type used to render this field
    /// </summary>
    public override Type ViewType => typeof(UiMSpeciesView);

    public UiMSpecies(string label, string dataDelegateName, string helpText = "", bool isAdvanced = false)
        : base("", label, helpText, isAdvanced, dataDelegateName: dataDelegateName)
    {
        
    }
}
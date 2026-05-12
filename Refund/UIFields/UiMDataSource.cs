namespace Refund.UIFields;

public class UiMDataSource : UiFieldBase
{
    /// <summary>
    /// Gets the Blazor component type used to render this field (UiIntView)
    /// </summary>
    public override Type ViewType => typeof(UiMDataSourceView);

    public UiMDataSource(string label, string dataDelegateName, string helpText = "", bool isAdvanced = false)
        : base("", label, helpText, isAdvanced, dataDelegateName: dataDelegateName)
    {
        
    }
}
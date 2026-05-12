using System;
using System.Reflection;

namespace Refund.UIFields;

/// <summary>
/// UI field for EMDB entry selection that displays a preview image.
/// </summary>

public class UiEmdbEntry : UiFieldBase
{
    /// <summary>
    /// Gets the view component type for this field.
    /// </summary>
    public override Type ViewType => typeof(UiEmdbEntryView);
    
    /// <summary>
    /// Minimum allowed EMDB entry number.
    /// </summary>
    public int Min { get; set; } = 1;

    /// <summary>
    /// Maximum allowed EMDB entry number.
    /// </summary>
    public int Max { get; set; } = 9999999;

    /// <summary>
    /// Constructor for EMDB entry UI field.
    /// </summary>
    /// <param name="cliName">CLI parameter name</param>
    /// <param name="label">UI label</param>
    /// <param name="helpText">Help text to display</param>
    public UiEmdbEntry(string cliName, string label, string helpText = "") : base(cliName, label, helpText)
    {
    }
}
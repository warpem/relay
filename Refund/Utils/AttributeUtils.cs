using System.Reflection;
using Refund.DataModel;
using Refund.UIFields;

namespace Refund.Utils;

/// <summary>
/// Provides utilities for working with job property attributes in the UI field system.
/// </summary>
public class AttributeUtils
{
    /// <summary>
    /// Organizes a job's properties into groups based on <see cref="UiFieldGroup"/> attributes,
    /// filtering properties by whether they are marked as advanced if required.
    /// </summary>
    /// <param name="node">The job object whose properties should be grouped.</param>
    /// <param name="showAdvanced">When true, includes properties marked with IsAdvanced=true. When false, filters them out.</param>
    /// <returns>
    /// A dictionary where keys are group names (from <see cref="UiFieldGroup.Label"/>) 
    /// and values are lists of <see cref="PropertyInfo"/> objects belonging to that group.
    /// Properties are only included if they have a <see cref="UiFieldBase"/> attribute.
    /// </returns>
    /// <remarks>
    /// This method is primarily used in job editor UIs to organize properties into collapsible sections.
    /// When a property has a <see cref="UiFieldGroup"/> attribute, it starts a new group.
    /// All subsequent properties (without their own group attribute) are placed in that group
    /// until another property with a <see cref="UiFieldGroup"/> attribute is encountered.
    /// </remarks>
    public static Dictionary<string, List<PropertyInfo>> GroupPropertiesByAttribute(Job node, bool showAdvanced = false)
    {
        var nodeType = node.GetType();
        var properties = nodeType.GetProperties().Where(_ => _.CustomAttributes.Any());

        var groupedProperties = new Dictionary<string, List<PropertyInfo>>();
        string currentGroupName = null;
        var currentGroup = new List<PropertyInfo>();

        foreach(var property in properties)
        {
            var groupAttributes = property.GetCustomAttributes(typeof(UiFieldGroup), true)
                .Cast<UiFieldGroup>()
                .FirstOrDefault();

            if(groupAttributes != null)
            {
                if(currentGroup.Count > 0 && currentGroupName != null)
                {
                    groupedProperties[currentGroupName] = [..currentGroup];
                    currentGroup.Clear();
                }

                currentGroupName = groupAttributes.Label;
            }

            var customAttribute = (UiFieldBase)property.GetCustomAttribute(typeof(UiFieldBase), true);

            if(customAttribute != null)
            {
                if(showAdvanced)
                {
                    currentGroup.Add(property);
                }
                else if(!customAttribute.IsAdvanced)
                {
                    currentGroup.Add(property);
                }
            }
        }

        if(currentGroup.Count > 0 && currentGroupName != null)
        {
            groupedProperties[currentGroupName] = new List<PropertyInfo>(currentGroup);
        }

        return groupedProperties;
    }
}
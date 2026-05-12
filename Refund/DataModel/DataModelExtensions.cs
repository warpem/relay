namespace Refund.DataModel;

/// <summary>
/// Provides extension methods for data model objects.
/// These utility methods enhance the functionality of model classes.
/// </summary>
public static class DataModelExtensions
{
    /// <summary>
    /// Performs a deep comparison between two objects by comparing all their property values.
    /// This method is more thorough than the default Equals implementation, which often just compares references.
    /// </summary>
    /// <param name="obj">The source object to compare</param>
    /// <param name="another">The target object to compare against</param>
    /// <returns>True if all property values are equal, false otherwise</returns>
    public static bool DeepCompare(this object obj, object another)
    {
        if(ReferenceEquals(obj, another))
            return true;

        if(obj == null || another == null)
            return false;

        if(obj.GetType() != another.GetType())
            return false;

        var result = true;

        foreach(var property in obj.GetType().GetProperties())
            try
            {
                var objValue = property.GetValue(obj);
                var anotherValue = property.GetValue(another);

                if (objValue != null && !objValue.Equals(anotherValue))
                    return false;
            }
            catch
            {
                return false;
            }

        return true;
    }
}
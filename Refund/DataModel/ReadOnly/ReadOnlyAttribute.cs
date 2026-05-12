namespace Refund.DataModel.ReadOnly;

/// <summary>
/// Attribute used to mark classes for which read-only wrappers should be generated.
/// This attribute is used by source generators to create read-only wrapper classes
/// that provide immutable views of mutable data model objects.
/// </summary>
public class GenerateReadOnlyAttribute : Attribute { }

/// <summary>
/// Attribute used to specify which mutable data model type a read-only wrapper is for.
/// This establishes the relationship between the wrapper class and the wrapped type.
/// </summary>
/// <param name="jobType">The type of the mutable data model object that this read-only wrapper represents</param>
public class ReadOnlyForAttribute(Type jobType) : Attribute
{
    /// <summary>
    /// Gets or sets the type of the mutable data model object that this read-only wrapper represents.
    /// </summary>
    public Type JobType { get; set; } = jobType;
}
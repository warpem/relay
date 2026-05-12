namespace Refund.Services;

/// <summary>
/// Provides thread-safe generation of unique long integer IDs for a specific type.
/// </summary>
/// <typeparam name="T">The type for which IDs are being generated</typeparam>
/// <remarks>
/// This service generates sequential, unique long integer IDs for a specific entity type.
/// IDs are incremented atomically and are guaranteed to be unique within a single application instance.
/// 
/// The generic type parameter T is used to create separate ID sequences for different entity types.
/// Note that the ID sequence is maintained in memory and will reset if the application restarts.
/// 
/// This service is similar to <see cref="UniqueIdGeneratorService{T}"/> but uses long instead of int,
/// which is useful for entity types that might require a larger ID range.
/// 
/// This service is thread-safe and can be used concurrently from multiple threads.
/// </remarks>
public class UniqueLongIdGeneratorService<T>
{
    // Resharper disable once StaticMemberInGenericType
    private static long lastId;

    // Resharper disable once StaticMemberInGenericType
    private static readonly object Locker = new();

    /// <summary>
    /// Gets or sets the last ID that was generated.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is negative or equal to long.MaxValue</exception>
    private static long LastId
    {
        get => lastId;
        set
        {
            if(value is < 0 or long.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value),
                    $"Provided number is out of range (value = {value})");
            }

            lastId = value;
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UniqueLongIdGeneratorService{T}"/> class.
    /// </summary>
    /// <param name="startId">The initial ID value (defaults to 0)</param>
    /// <remarks>
    /// The service will generate IDs starting from startId + 1.
    /// </remarks>
    public UniqueLongIdGeneratorService(long startId = 0)
    {
        LastId = startId;
    }

    /// <summary>
    /// Generates a new unique ID.
    /// </summary>
    /// <returns>A unique long integer ID</returns>
    /// <remarks>
    /// This method is thread-safe and guarantees that each call will return a unique ID.
    /// IDs are generated sequentially, starting from the value provided in the constructor plus one.
    /// </remarks>
    public long GenerateId()
    {
        lock(Locker)
        {
            return LastId++;
        }
    }
}
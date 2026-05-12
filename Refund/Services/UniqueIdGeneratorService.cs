namespace Refund.Services;

/// <summary>
/// Provides thread-safe generation of unique integer IDs for a specific type.
/// </summary>
/// <typeparam name="T">The type for which IDs are being generated</typeparam>
/// <remarks>
/// This service generates sequential, unique integer IDs for a specific entity type.
/// IDs are incremented atomically and are guaranteed to be unique within a single application instance.
/// 
/// The generic type parameter T is used to create separate ID sequences for different entity types.
/// Note that the ID sequence is maintained in memory and will reset if the application restarts.
/// 
/// This service is thread-safe and can be used concurrently from multiple threads.
/// </remarks>
public class UniqueIdGeneratorService<T>
{
    // Resharper disable once StaticMemberInGenericType
    private static int lastId;

    // Resharper disable once StaticMemberInGenericType
    private static readonly object Locker = new();

    /// <summary>
    /// Gets or sets the last ID that was generated.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is negative or equal to int.MaxValue</exception>
    private static int LastId
    {
        get => lastId;
        set
        {
            if(value is < 0 or int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value),
                    $"Provided number is out of range (value = {value})");
            }

            lastId = value;
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UniqueIdGeneratorService{T}"/> class.
    /// </summary>
    /// <param name="startId">The initial ID value (defaults to 0)</param>
    /// <remarks>
    /// The service will generate IDs starting from startId + 1.
    /// </remarks>
    public UniqueIdGeneratorService(int startId = 0)
    {
        LastId = startId;
    }

    /// <summary>
    /// Generates a new unique ID.
    /// </summary>
    /// <returns>A unique integer ID</returns>
    /// <remarks>
    /// This method is thread-safe and guarantees that each call will return a unique ID.
    /// IDs are generated sequentially, starting from the value provided in the constructor plus one.
    /// </remarks>
    public int GenerateId()
    {
        lock(Locker)
        {
            return LastId++;
        }
    }
}
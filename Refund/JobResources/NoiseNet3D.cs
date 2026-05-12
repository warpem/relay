using Refund.DataModel;

namespace Refund.JobResources;

public class NoiseNet3D : Resource
{
    /// <summary>
    /// Path to the .pt model file.
    /// </summary>
    public readonly string ModelPath;

    /// <summary>
    /// Creates a new NoiseNet3D resource with a path to the model file.
    /// </summary>
    /// <param name="modelPath">Path to the model file</param>
    public NoiseNet3D(string modelPath)
    {
        ModelPath = modelPath;
    }

    /// <summary>
    /// Returns a collection of downloadable resources associated with this mask.
    /// </summary>
    /// <returns>Collection of resources that can be downloaded by the user</returns>
    public override IEnumerable<Downloadable> GetDownloadables()
    {
        List<Downloadable> result = new();
            
        if (!string.IsNullOrWhiteSpace(ModelPath))
            result.Add(new Downloadable("Model", "", ModelPath));

        return result;
    }
}
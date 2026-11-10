namespace ComponentDev.Services;

public sealed partial record PreviewSettings
{
    public bool PickingEnabled { get; init; } = true;
    /// <summary>Minimum center-to-center distance as a fraction of particle diameter.</summary>
    public decimal MinSeparation { get; init; } = .8m;
}

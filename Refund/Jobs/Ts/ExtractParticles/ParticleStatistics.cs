using System.Text.Json.Serialization;

namespace Refund.Jobs.Ts.ExtractParticles;

/// <summary>
/// Represents statistics about extracted particles.
/// </summary>
[Serializable]
public class ParticleStatistics
{
    /// <summary>
    /// Maps tilt series names to the number of particles extracted from them
    /// </summary>
    [JsonPropertyName("tiltSeriesParticleCounts")]
    public Dictionary<string, int> TiltSeriesParticleCounts { get; set; } = new();
    
    /// <summary>
    /// Total number of particles extracted
    /// </summary>
    [JsonPropertyName("totalParticles")]
    public int TotalParticles { get; set; }
    
    /// <summary>
    /// The average number of particles per tilt series
    /// </summary>
    [JsonPropertyName("averageParticlesPerSeries")]
    public double AverageParticlesPerSeries { get; set; }
}
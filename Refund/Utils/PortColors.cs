using Refund.JobResources;

namespace Refund.Utils;

public static class PortColors
{
    private static readonly string Default = "#8890A0";

    private static readonly Dictionary<Type, string> Colors = new()
    {
        // Raw data inputs (warm)
        [typeof(DataSetFs)] = "#D07040",
        [typeof(DataSetTs)] = "#C89030",

        // Image collections (cool blues)
        [typeof(MicrographSet)] = "#3898C8",
        [typeof(TiltSeriesSet)] = "#28A098",

        // 3D volumes (greens)
        [typeof(TomogramSet)] = "#58A848",
        [typeof(Map)] = "#90A830",
        [typeof(MapList)] = "#2DA070",

        // Particles & classification (blue-purple)
        [typeof(ParticleSet)] = "#4878D0",
        [typeof(TemplateSet)] = "#8860B8",

        // Masks (rose)
        [typeof(Mask)] = "#D05878",

        // M refinement (magenta family)
        [typeof(MPopulation)] = "#B850A0",
        [typeof(MSpecies)] = "#9078A0",
        [typeof(MDataSource)] = "#7088A0",

        // ML models (muted earth tones)
        [typeof(NoiseNet3D)] = "#808860",
        [typeof(MissAlignmentModel)] = "#888068",
        [typeof(ContinuableClass3D)] = "#887050",
    };

    private static readonly Dictionary<string, string> ColorsByName =
        Colors.ToDictionary(kvp => kvp.Key.Name, kvp => kvp.Value);

    public static string Get(Type type) => Colors.GetValueOrDefault(type, Default);

    public static string Get(string typeName) => ColorsByName.GetValueOrDefault(typeName, Default);
}
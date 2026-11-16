using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Refund.Services.Warp;

/// <summary>
/// EPU's beam shift for one exposure (microscopeData/optics/BeamShift, in the microscope's arbitrary units) and the
/// EPU session it was recorded in, which keeps groups from different sessions apart: re-centring the beam between
/// sessions moves every position.
/// </summary>
public sealed record WarpBeamShift(double X, double Y, string Session = "");

/// <summary>
/// Reads beam shift from Thermo Fisher EPU's per-exposure XML and groups exposures by it into optics groups, so groups
/// can be refined separately (beam tilt, higher-order aberrations) later.
/// </summary>
/// <remarks>
/// EPU writes FoilHole_&lt;hole&gt;_Data_&lt;a&gt;_&lt;b&gt;_&lt;date&gt;_&lt;time&gt;.xml next to the movie, whose name adds a camera suffix
/// (_Fractions.tiff, _Fractions.mrc, _EER.eer). Facilities often store movies apart from the XMLs, so a metadata folder
/// can be given instead; it is searched recursively and matched on the FoilHole_…_&lt;time&gt; part of the name, which
/// survives prefixes added by copies. *_Fractions.xml files hold only fraction metadata and are skipped.
/// </remarks>
public static partial class WarpEpu
{
    [GeneratedRegex(@"FoilHole_\d+_Data_\d+_\d+_\d{8}_\d{6}", RegexOptions.IgnoreCase)]
    private static partial Regex ExposureCore();
    [GeneratedRegex(@"_(fractions|eer)$", RegexOptions.IgnoreCase)]
    private static partial Regex CameraSuffix();

    /// <summary>The movie's name without extension and camera suffix: the name of its EPU XML.</summary>
    public static string Stem(string movieFileName) => CameraSuffix().Replace(Path.GetFileNameWithoutExtension(movieFileName), "");

    /// <summary>What a movie and its XML share: the FoilHole_…_&lt;time&gt; core, or the stem for names without one.</summary>
    public static string Key(string fileName)
    {
        string stem = Stem(fileName);
        var core = ExposureCore().Match(stem);
        return core.Success ? core.Value.ToLowerInvariant() : stem.ToLowerInvariant();
    }

    /// <summary>The EPU XML beside the movie, or null.</summary>
    public static string Beside(string moviePath)
    {
        string folder = Path.GetDirectoryName(moviePath);
        if (string.IsNullOrEmpty(folder)) return null;
        string xml = Path.Combine(folder, Stem(Path.GetFileName(moviePath)) + ".xml");
        return File.Exists(xml) ? xml : null;
    }

    /// <summary>The EPU session folder (the one holding Images-Disc*), or "" when the path shows none.</summary>
    public static string SessionOf(string xmlPath)
    {
        string folder = Path.GetDirectoryName(xmlPath);
        for (string current = folder; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if (Path.GetFileName(current).StartsWith("Images-Disc", StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(current) ?? "";
        return "";
    }

    /// <summary>The exposure's beam shift, or null when the file has none or cannot be read.</summary>
    public static WarpBeamShift Read(string xmlPath)
    {
        try
        {
            var root = XDocument.Load(xmlPath).Root;
            var shift = root?.Elements().FirstOrDefault(e => e.Name.LocalName == "microscopeData")?
                .Elements().FirstOrDefault(e => e.Name.LocalName == "optics")?
                .Elements().FirstOrDefault(e => e.Name.LocalName == "BeamShift");
            double? Value(string name) => double.TryParse(shift?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value,
                NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : null;
            return Value("_x") is { } x && Value("_y") is { } y ? new(x, y, SessionOf(xmlPath)) : null;
        }
        catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Finds exposures' XMLs: beside each movie, else in a metadata folder indexed once, on the first movie not found
    /// beside its XML.
    /// </summary>
    public sealed class Finder(string metadataFolder)
    {
        private Dictionary<string, string> _index;

        public string Find(string moviePath)
        {
            if (Beside(moviePath) is { } beside) return beside;
            if (string.IsNullOrWhiteSpace(metadataFolder) || !Directory.Exists(metadataFolder)) return null;
            _index ??= Index(metadataFolder);
            return _index.GetValueOrDefault(Key(Path.GetFileName(moviePath)));
        }

        private static Dictionary<string, string> Index(string folder)
        {
            var index = new Dictionary<string, string>();
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
            foreach (string xml in Directory.EnumerateFiles(folder, "*.xml", options))
            {
                string name = Path.GetFileNameWithoutExtension(xml);
                if (name.EndsWith("_Fractions", StringComparison.OrdinalIgnoreCase) || !ExposureCore().IsMatch(name)) continue;
                index.TryAdd(Key(name), xml);
            }
            return index;
        }
    }

    /// <summary>
    /// Optics group of each exposure, 1-based in order of first appearance; 0 without a beam shift. In acquisition
    /// order, an exposure joins the nearest group of its EPU session whose mean position is within
    /// <paramref name="radius"/>, otherwise it starts a new one. An exposure's group depends only on the exposures
    /// before it, so groups given out never change as movies arrive.
    /// </summary>
    public static int[] Group(IReadOnlyList<WarpBeamShift> shifts, double radius)
    {
        var groups = new List<(string Session, double SumX, double SumY, int Count)>();
        var result = new int[shifts.Count];
        for (int i = 0; i < shifts.Count; i++)
        {
            if (shifts[i] is not { } s) continue;
            int best = -1; double distance = radius;
            for (int g = 0; g < groups.Count; g++)
            {
                var group = groups[g];
                if (group.Session != s.Session) continue;
                double d = Math.Sqrt(Math.Pow(s.X - group.SumX / group.Count, 2) + Math.Pow(s.Y - group.SumY / group.Count, 2));
                if (d <= distance) { best = g; distance = d; }
            }
            if (best < 0) { groups.Add((s.Session, s.X, s.Y, 1)); best = groups.Count - 1; }
            else { var g = groups[best]; groups[best] = (g.Session, g.SumX + s.X, g.SumY + s.Y, g.Count + 1); }
            result[i] = best + 1;
        }
        return result;
    }
}

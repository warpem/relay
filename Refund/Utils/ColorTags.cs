namespace Refund.Utils;

/// <summary>
/// Color tags users can put on jobs, folders and factory instances. Stored on the item as the
/// hex string; the order here is the order of the Color menu and of the Color sort.
/// </summary>
public static class ColorTags
{
    public static readonly IReadOnlyList<(string Hex, string Label)> Palette =
    [
        ("#F5A8A8", "Red"),
        ("#F5CDA8", "Orange"),
        ("#F5E6A8", "Yellow"),
        ("#A8F5B8", "Green"),
        ("#A8E6F5", "Teal"),
        ("#A8B8F5", "Blue"),
        ("#CDA8F5", "Purple"),
        ("#F5A8E6", "Pink")
    ];

    /// <summary>
    /// Position of a tag in <see cref="Palette"/>. Tags outside the palette sort after it,
    /// and empty tags after those.
    /// </summary>
    public static int SortIndex(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return Palette.Count + 1;

        for (int i = 0; i < Palette.Count; i++)
            if (string.Equals(Palette[i].Hex, tag.Trim(), StringComparison.OrdinalIgnoreCase))
                return i;

        return Palette.Count;
    }
}

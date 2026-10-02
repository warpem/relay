using Microsoft.AspNetCore.Components;

namespace Relay.Components.HeroIcon;

/// <summary>
/// Renders an entity's hero image at a fixed square size.
/// Fluent emoji are preferred; web image sources render as images; unknown glyphs fall back to text.
/// The square is always reserved so rows and headers stay aligned when no image is set.
/// </summary>
public partial class HeroIcon : ComponentBase
{
    /// <summary>
    /// Emoji glyph or image source, typically an entity's HeroImage.
    /// </summary>
    [Parameter]
    public string Value { get; set; }

    /// <summary>
    /// Side length in pixels.
    /// </summary>
    [Parameter]
    public int Size { get; set; } = 40;

    /// <summary>
    /// Additional CSS class for the container.
    /// </summary>
    [Parameter]
    public string Class { get; set; }

    /// <summary>
    /// Longest string still treated as a raw emoji glyph (ZWJ sequences can be long).
    /// </summary>
    private const int MaxGlyphLength = 16;

    private static bool IsImageSource(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            return true;

        if (value.StartsWith('/') && !value.StartsWith("//"))
            return true;

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}

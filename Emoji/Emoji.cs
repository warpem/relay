using System;
using System.Collections.Generic;
using System.Linq;
using FEmoji = Microsoft.FluentUI.AspNetCore.Components.Emoji;

namespace Relay.Emoji;

/// <summary>
/// Library of emoji data for the application with categorization, search, and random selection capabilities.
/// Used by the EmojiSelector component for displaying and selecting emojis, and by various model creation
/// screens for generating random emoji as default hero images.
/// </summary>
public static partial class EmojiLibrary
{
    private static readonly Dictionary<string, List<EmojiInfo>> ByKeyword = new();

    static EmojiLibrary()
    {
        string[] badEmoji = ByGlyph.Where(kvp => kvp.Value.FluentEmoji == null).Select(kvp => kvp.Key).ToArray();
        foreach (var key in badEmoji)
            ByGlyph.Remove(key);
        
        foreach (var emoji in ByGlyph.Values)
        {
            foreach (var keyword in emoji.Keywords)
            {
                var key = keyword.ToLowerInvariant();
                if (!ByKeyword.ContainsKey(key))
                    ByKeyword[key] = new List<EmojiInfo>();
                ByKeyword[key].Add(emoji);
            }
        }
    }

    /// <summary>
    /// Finds an emoji by its glyph character.
    /// </summary>
    /// <param name="glyph">The emoji glyph to find</param>
    /// <returns>The corresponding EmojiInfo if found, null otherwise</returns>
    public static EmojiInfo FindByGlyph(string glyph)
    {
        if (glyph == null)
            return null;
        
        return ByGlyph.GetValueOrDefault(glyph);
    }

    /// <summary>
    /// Finds emojis that match a specific keyword.
    /// </summary>
    /// <param name="keyword">The keyword to search for</param>
    /// <returns>Collection of matching emoji</returns>
    public static IEnumerable<EmojiInfo> FindByKeyword(string keyword) =>
        ByKeyword.TryGetValue(keyword.ToLowerInvariant(), out var emojis) ? emojis : Array.Empty<EmojiInfo>();

    /// <summary>
    /// Gets all emoji group categories.
    /// Used in the EmojiSelector component to organize emojis by category.
    /// </summary>
    public static IEnumerable<string> Groups =>
        new[] { "Activities", "Animals & Nature", "Flags", "Food & Drink", "Objects", "People & Body", "Smileys & Emotion", "Symbols", "Travel & Places" };

    /// <summary>
    /// Gets all emojis within a specified group/category.
    /// Used in the EmojiSelector component to display emojis by their category.
    /// </summary>
    /// <param name="group">The group name to filter by</param>
    /// <returns>Collection of emojis in the specified group</returns>
    public static IEnumerable<EmojiInfo> GetByGroup(string group) =>
        ByGlyph.Values.Where(e => e.Group == group).OrderBy(e => e.Unicode);

    /// <summary>
    /// Collection of emojis that are safe to use for random selection.
    /// Contains a curated subset of positive and neutral emojis.
    /// </summary>
    public static IEnumerable<EmojiInfo> SafeForRandom => ("😀 😃 😄 😁 😆 🥹 😅 ☺️ 😊 🙂 🙃 😌 😍 🥰 😋 😛 🤪 🤓 😎 🥸 🤩 🥳 👻 👽 🤖 🎃 😺 😸 😼 🐶 " +
                                                           "🐱 🐭 🐹 🐰 🦊 🐻 🐼 🐻‍❄️ 🐨 🐯 🦁 🐸 🐵 🐔 🐧 🐣 🐥 🦉 🦄 🐝 🦋 🐌 🐙 🐠 🦭 🍀 🌳 🌲 🌵 🐳 " +
                                                           "🌴 🍁 🐚 🪸 🔥 🌪️ 🌞 🌝 🌼 🌻 ⚡️ ⭐️ 🌟 ⛄️ 🍏 🍎 🍐 🍊 🍉 🍍 🥝 🥨 🍔 🍕 🥗 🍭 🧁 🍦 🍿 🍩 " +
                                                           "🍪 ☕️ 🧉 🍾 ⚽️ 🏀 🏈 ⚾️ 🎾 🏐 🎱 🪃 🏆 🥇 🎸 🎲 🚗 🚕 🚙 🚌 🚎 🚜 🚃 ✈️ 🚁 ⛵️ ⚓️ 🏰 ⛱️ 🏝️ " +
                                                           "⛺️ 🏠 🏛️ 🌇 📀 💾 📷 ☎️ 🧭 ⏰ ⏳ 🔋 💡 🕯️ 🔮 🔬 💊 🧬 🦠 🧪 🔑 🎁 🎈 🪩 🤯 📯 📆 📒 🖌️ ✏️ " +
                                                           "❤️ ☢️ ♠️ ♣️ ♥️ ♦️ 🔝 🔛 📣 💫 🌊 ❄️").Split(" ", StringSplitOptions.RemoveEmptyEntries)
                                                                                                   .Where(g => ByGlyph.ContainsKey(g))
                                                                                                   .Select(g => ByGlyph[g]);
    
    /// <summary>
    /// Gets a random emoji from the SafeForRandom collection.
    /// Used in creation screens (for projects, spaces, views) to generate default hero images.
    /// </summary>
    /// <returns>A random emoji</returns>
    public static EmojiInfo GetRandom() => SafeForRandom.ElementAt(new Random().Next(SafeForRandom.Count()));
}

/// <summary>
/// Represents a single emoji with all its metadata.
/// Used throughout the application for emoji display, selection, and categorization.
/// The Glyph property is commonly used in UI components and as hero images.
/// </summary>
public class EmojiInfo
{
    /// <summary>
    /// The name of the emoji
    /// </summary>
    public string Name { get; }
    
    /// <summary>
    /// The actual emoji character (Unicode glyph)
    /// Used as hero images in project, space, and view models
    /// </summary>
    public string Glyph { get; }
    
    /// <summary>
    /// The category/group this emoji belongs to
    /// </summary>
    public string Group { get; }
    
    /// <summary>
    /// The Unicode code point for the emoji
    /// Used for ordering emojis in selector components
    /// </summary>
    public string Unicode { get; }
    
    /// <summary>
    /// Keywords associated with this emoji
    /// Used for searching in the EmojiSelector component
    /// </summary>
    public IReadOnlyList<string> Keywords { get; }
    
    /// <summary>
    /// Whether this emoji supports skin tone variants
    /// </summary>
    public bool HasSkinTones { get; }
    
    /// <summary>
    /// The class name in the Fluent UI emoji system
    /// </summary>
    public string FluentClassName { get; }
    
    /// <summary>
    /// The group name in the Fluent UI emoji system
    /// </summary>
    public string FluentGroupName { get; }
    
    /// <summary>
    /// Reference to the Fluent UI emoji component
    /// </summary>
    public FEmoji FluentEmoji { get; }

    /// <summary>
    /// Gets the Fluent UI type for an emoji by its group and name
    /// </summary>
    /// <param name="group">The Fluent group name</param>
    /// <param name="name">The emoji name</param>
    /// <returns>The Type representing the Fluent emoji</returns>
    public static Type GetFluentType(string group, string name)
    {
        var type = Type.GetType($"Microsoft.FluentUI.AspNetCore.Components.Emojis.{group}.Color.Default+{name}, Microsoft.FluentUI.AspNetCore.Components.Emojis.{group}");
        //if (type == null)
        //{
        //    throw new InvalidOperationException($"Failed to load fluent type {group}+Color+Default+{name}");
        //}
        return type;
    }

    /// <summary>
    /// Creates a new emoji info instance with all its metadata
    /// </summary>
    public EmojiInfo(string name, string glyph, string group, string unicode, string[] keywords, bool hasSkinTones, string fluentClassName, string fluentGroupName, FEmoji fluentEmoji)
    {
        Name = name;
        Glyph = glyph;
        Group = group;
        Unicode = unicode;
        Keywords = keywords;
        HasSkinTones = hasSkinTones;
        FluentClassName = fluentClassName;
        FluentGroupName = fluentGroupName;
        FluentEmoji = fluentEmoji;
    }
}
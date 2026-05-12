using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using EmojiInfo = Relay.Emoji.EmojiInfo;

namespace Refund.Components.Emoji;

/// <summary>
/// A button component that allows users to select an emoji from a popup emoji selector.
/// When clicked, it opens an <see cref="EmojiSelector"/> component and passes the selected
/// emoji back to the parent component.
/// </summary>
public partial class EmojiSelectionButton : ComponentBase
{
    /// <summary>
    /// The currently selected emoji glyph (Unicode character) to display in the button.
    /// </summary>
    [Parameter, EditorRequired]
    public string Glyph { get; set; } = "";
    
    /// <summary>
    /// Event callback that is triggered when the user selects a new emoji.
    /// The selected emoji's glyph (Unicode character) is passed as the parameter.
    /// </summary>
    [Parameter]
    public EventCallback<string> GlyphChanged { get; set; }

    /// <summary>
    /// Optional size parameter for the button in pixels. If null, the default size is used.
    /// </summary>
    [Parameter]
    public int? Size { get; set; } = null; 
    
    /// <summary>
    /// Determines whether to show a border around the emoji button.
    /// </summary>
    [Parameter]
    public bool ShowBorder { get; set; } = false;
    
    /// <summary>
    /// Controls the horizontal positioning of the emoji selector dropdown.
    /// Uses FluentUI's HorizontalPosition enum for alignment.
    /// </summary>
    [Parameter]
    public HorizontalPosition? HorizontalPosition { get; set; } = null;
    
    /// <summary>
    /// Controls the vertical positioning of the emoji selector dropdown.
    /// Uses FluentUI's VerticalPosition enum for alignment.
    /// </summary>
    [Parameter]
    public VerticalPosition? VerticalPosition { get; set; } = null;
    
    /// <summary>
    /// Determines whether the dropdown selector is inset horizontally.
    /// When true, the popup will try to stay within the horizontal bounds of its container.
    /// </summary>
    [Parameter]
    public bool HorizontalInset { get; set; } = true;
    
    /// <summary>
    /// Determines whether the button is disabled.
    /// When true, the button is displayed with reduced opacity and doesn't respond to clicks.
    /// </summary>
    [Parameter]
    public bool Disabled { get; set; } = false;
    
    /// <summary>
    /// Unique identifier for this component instance, used for DOM identification.
    /// </summary>
    private string _id = Guid.NewGuid().ToString();
    
    /// <summary>
    /// Tracks whether the emoji selector dropdown is currently open.
    /// </summary>
    private bool _isEmojiSelectorOpen = false;

    /// <summary>
    /// Handles the emoji selection event from the EmojiSelector component.
    /// Closes the selector, invokes the GlyphChanged callback with the selected emoji's glyph,
    /// and triggers a UI update.
    /// </summary>
    /// <param name="emoji">The selected emoji information</param>
    private async Task HandleEmojiChanged(EmojiInfo emoji)
    {
        _isEmojiSelectorOpen = false;
        await GlyphChanged.InvokeAsync(emoji.Glyph);
        
        await InvokeAsync(StateHasChanged);
    }
    
    /// <summary>
    /// Opens the emoji selector if the button is not disabled.
    /// </summary>
    private void OpenEmojiSelector()
    {
        if (!Disabled)
        {
            _isEmojiSelectorOpen = true;
        }
    }
}
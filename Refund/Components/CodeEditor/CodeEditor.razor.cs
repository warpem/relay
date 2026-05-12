using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using Refund.Services.Core.Session;

namespace Refund.Components.CodeEditor;

/// <summary>
/// A syntax-highlighting code editor component with Prism.js integration.
/// This component extends InputBase to provide form binding capabilities while adding
/// syntax highlighting, line numbers, and code editing features.
/// </summary>
public partial class CodeEditor : InputBase<string>, IAsyncDisposable
{
    /// <summary>
    /// JS runtime used for interacting with JavaScript functionality.
    /// </summary>
    [Inject] private IJSRuntime JS { get; set; } = default!;
    
    /// <summary>
    /// Session service that provides access to user preferences like theme.
    /// </summary>
    [Inject] private RelaySession Session { get; set; } = default!;

    /// <summary>
    /// Programming language for syntax highlighting (e.g., "bash", "csharp", "python").
    /// Corresponds to language identifiers supported by Prism.js.
    /// </summary>
    [Parameter] public string Language { get; set; } = "bash";
    
    /// <summary>
    /// Placeholder text to display when the editor is empty.
    /// </summary>
    [Parameter] public string Placeholder { get; set; } = "";
    
    /// <summary>
    /// Whether the editor is disabled/read-only.
    /// </summary>
    [Parameter] public bool IsDisabled { get; set; }
    
    /// <summary>
    /// Additional CSS class to apply to the editor container.
    /// </summary>
    [Parameter] public string? CssClass { get; set; }

    /// <summary>
    /// Reference to the PRE element that contains the highlighted code.
    /// </summary>
    private ElementReference preElement;
    
    /// <summary>
    /// Reference to the CODE element that displays the syntax-highlighted code.
    /// </summary>
    private ElementReference codeElement;
    
    /// <summary>
    /// Reference to the hidden TEXTAREA element that captures the user input.
    /// </summary>
    private ElementReference textareaElement;
    
    /// <summary>
    /// Reference to the imported JavaScript module for editor functionality.
    /// </summary>
    private IJSObjectReference? module;
    
    /// <summary>
    /// Indicates whether the component has been fully initialized with JS interop.
    /// </summary>
    private bool isInitialized;
    
    /// <summary>
    /// Reference to this component instance for JS callbacks.
    /// </summary>
    private DotNetObjectReference<CodeEditor>? dotNetRef;
    
    /// <summary>
    /// Tracks the last known value to prevent unnecessary updates.
    /// </summary>
    private string? lastKnownValue;

    /// <summary>
    /// Event raised when the cursor position changes in the editor.
    /// </summary>
    [Parameter] public EventCallback<(int selectionStart, int selectionEnd)> CursorPositionChanged { get; set; }

    /// <summary>
    /// Initializes the component by subscribing to theme change events.
    /// </summary>
    protected override async Task OnInitializedAsync()
    {
        Session.OnThemeChanged += OnThemeChangedAsync;
        await base.OnInitializedAsync();
    }

    /// <summary>
    /// Handles theme changes by updating the editor's appearance.
    /// Updates the syntax highlighting theme based on whether dark mode is enabled.
    /// </summary>
    private async Task OnThemeChangedAsync()
    {
        if (isInitialized && module != null)
        {
            await module.InvokeVoidAsync("setTheme", Session.ColorTheme == ColorTheme.Dark);
        }
    }

    /// <summary>
    /// Handles changes to the component's parameters.
    /// Detects changes to the Value property and updates the editor's content.
    /// </summary>
    /// <param name="parameters">The parameters to evaluate</param>
    public override async Task SetParametersAsync(ParameterView parameters)
    {
        var previousValue = Value;
        
        await base.SetParametersAsync(parameters);
        
        if (isInitialized && Value != previousValue && Value != lastKnownValue)
        {
            try
            {
                if (module != null)
                {
                    await module.InvokeVoidAsync("setValue", textareaElement, Value ?? "");
                    lastKnownValue = Value;
                }
            }
            catch (JSException)
            {
                isInitialized = false;
            }
        }
    }

    /// <summary>
    /// Parses input value from string format, required by InputBase.
    /// Since we're working with string values directly, parsing is trivial.
    /// </summary>
    /// <param name="value">The value to parse</param>
    /// <param name="result">The parsed result</param>
    /// <param name="validationErrorMessage">Any validation error message</param>
    /// <returns>True if parsing succeeded</returns>
    protected override bool TryParseValueFromString(string? value, out string result, out string validationErrorMessage)
    {
        result = value ?? string.Empty;
        validationErrorMessage = string.Empty;
        return true;
    }

    /// <summary>
    /// Initializes the JavaScript interop functionality after the component has rendered.
    /// Sets up the code editor, syntax highlighting, and event listeners.
    /// </summary>
    /// <param name="firstRender">Whether this is the first time the component has rendered</param>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        try 
        {
            if (firstRender)
            {
                dotNetRef = DotNetObjectReference.Create(this);
                module = await JS.InvokeAsync<IJSObjectReference>(
                    "import", "./_content/Refund/Components/CodeEditor/CodeEditor.razor.js");
                
                await module.InvokeVoidAsync("initialize", 
                    textareaElement, 
                    preElement, 
                    codeElement, 
                    dotNetRef,
                    Session.ColorTheme == ColorTheme.Dark,
                    Value ?? "");

                lastKnownValue = Value;
                isInitialized = true;
            }
        }
        catch (JSException) 
        {
            isInitialized = false;
        }
    }

    /// <summary>
    /// Invoked by JavaScript when the editor's value changes.
    /// Updates the component's state and notifies listeners of the value change.
    /// </summary>
    /// <param name="newValue">The new editor content</param>
    [JSInvokable]
    public async Task OnValueChanged(string newValue)
    {
        lastKnownValue = newValue;
        CurrentValue = newValue;
        await ValueChanged.InvokeAsync(newValue);
    }

    /// <summary>
    /// Invoked by JavaScript when the cursor position changes in the editor.
    /// Notifies listeners of the cursor position change.
    /// </summary>
    /// <param name="selectionStart">The start position of the selection</param>
    /// <param name="selectionEnd">The end position of the selection</param>
    [JSInvokable]
    public async Task OnCursorPositionChanged(int selectionStart, int selectionEnd)
    {
        await CursorPositionChanged.InvokeAsync((selectionStart, selectionEnd));
    }

    /// <summary>
    /// Sets the cursor position in the editor.
    /// </summary>
    /// <param name="selectionStart">The start position of the selection</param>
    /// <param name="selectionEnd">The end position of the selection (optional, defaults to selectionStart for cursor placement)</param>
    public async Task SetCursorPositionAsync(int selectionStart, int? selectionEnd = null)
    {
        if (isInitialized && module != null)
        {
            try
            {
                if (selectionEnd.HasValue)
                {
                    await module.InvokeVoidAsync("setCursorPosition", textareaElement, selectionStart, selectionEnd.Value);
                }
                else
                {
                    await module.InvokeVoidAsync("setCursorPosition", textareaElement, selectionStart);
                }
            }
            catch (JSException)
            {
                // Ignore JS exceptions during cursor positioning
            }
        }
    }

    /// <summary>
    /// Cleans up resources when the component is disposed.
    /// Removes event listeners, disposes JavaScript interop, and unsubscribes from theme changes.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (module is not null)
            {
                await module.InvokeVoidAsync("cleanup", textareaElement);
                await module.DisposeAsync();
            }
            
            if (dotNetRef is not null)
            {
                dotNetRef.Dispose();
            }

            Session.OnThemeChanged -= OnThemeChangedAsync;
        }
        catch
        {
            // Ensure we don't throw during disposal
        }
    }
}
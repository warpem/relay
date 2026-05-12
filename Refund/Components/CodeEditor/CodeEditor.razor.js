/**
 * JavaScript module for the CodeEditor component.
 * 
 * This module provides the client-side functionality for the CodeEditor component,
 * including syntax highlighting, scrolling synchronization, and event handling.
 * It relies on Prism.js for syntax highlighting.
 */

// Map to track editor instances and their associated state
const instances = new Map();

// Reference to the current theme stylesheet link element
let currentThemeLink = null;

/**
 * Checks if Prism.js library is loaded and available.
 * 
 * @returns {boolean} True if Prism is loaded and the highlightElement function is available
 */
function isPrismAvailable() {
    return window.Prism && typeof window.Prism.highlightElement === 'function';
}

/**
 * Sets up custom syntax highlighting for Relay variables.
 * 
 * Adds highlighting for variables in the format {{variableName}} within bash scripts,
 * both as standalone tokens and within comments.
 */
function setupCustomGrammar() {    
    // First define relayvariable as a standalone token
    Prism.languages.insertBefore('bash', 'function', {
        'relayvariable': {
            pattern: /\{\{[^}]+\}\}/
        }
    });

    // Then modify the existing comment token to include relayvariable
    Prism.languages.bash.comment.inside = {
        'relayvariable': {
            pattern: /\{\{[^}]+\}\}/
        }
    };
}

/**
 * Escapes HTML special characters to prevent XSS.
 * 
 * @param {string} text - Raw text to be escaped
 * @returns {string} HTML-escaped text
 */
function escapeHtml(text) {
    return text
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;")
        .replace(/'/g, "&#039;");
}

/**
 * Creates a debounced version of a function.
 * 
 * @param {Function} func - The function to debounce
 * @param {number} wait - Debounce delay in milliseconds
 * @returns {Function} Debounced function
 */
function debounce(func, wait) {
    let timeout;
    return function executedFunction(...args) {
        const later = () => {
            clearTimeout(timeout);
            func(...args);
        };
        clearTimeout(timeout);
        timeout = setTimeout(later, wait);
    };
}

/**
 * Updates the displayed code content with syntax highlighting.
 * 
 * @param {string} value - The code content to display
 * @param {Object} state - The editor instance state
 */
function updateContent(value, state) {
    // Update content with a trailing newline for proper line number rendering
    const content = (value?.trimEnd() ?? "") + "\n";
    state.codeElement.innerHTML = escapeHtml(content);

    // Apply syntax highlighting
    window.Prism.highlightElement(state.codeElement);
}

/**
 * Creates and configures a new editor instance.
 * 
 * @param {HTMLTextAreaElement} textareaElement - The textarea element for capturing input
 * @param {HTMLElement} preElement - The pre element for displaying highlighted code
 * @param {HTMLElement} codeElement - The code element within the pre element
 * @param {DotNetReference} dotNetRef - Reference to .NET component for callbacks
 * @throws {Error} If Prism.js is not available
 */
function createInstance(textareaElement, preElement, codeElement, dotNetRef) {
    if (!isPrismAvailable()) {
        throw new Error('Prism.js is not loaded');
    }

    // Create state object to hold references and handlers
    const state = {
        preElement,
        codeElement,
        dotNetRef,
        scrollHandler: null,
        inputHandler: null,
        notifyBlazor: null,
        blurHandler: null,
        selectionHandler: null,
        notifyCursorPosition: null
    };

    // Create debounced notification function to avoid excessive updates
    state.notifyBlazor = debounce((value) => {
        dotNetRef.invokeMethodAsync('OnValueChanged', value);
    }, 150);  // 150ms debounce

    // Create debounced cursor position notification function
    state.notifyCursorPosition = debounce((selectionStart, selectionEnd) => {
        dotNetRef.invokeMethodAsync('OnCursorPositionChanged', selectionStart, selectionEnd);
    }, 100);  // 100ms debounce for cursor position

    // Set up scroll synchronization to keep highlighted view aligned with textarea
    state.scrollHandler = () => {
        requestAnimationFrame(() => {
            preElement.scrollTop = textareaElement.scrollTop;
            preElement.scrollLeft = textareaElement.scrollLeft;
        });
    };

    // Set up input handling to update highlighting and notify .NET component
    state.inputHandler = () => {
        const value = textareaElement.value;

        // Immediately update visual content
        updateContent(value, state);

        // Debounce the Blazor notification
        state.notifyBlazor(value);
    };

    // Set up blur handling for immediate notification when focus leaves the editor
    state.blurHandler = () => {
        const value = textareaElement.value;
        dotNetRef.invokeMethodAsync('OnValueChanged', value);
    };

    // Set up selection change handling to track cursor position
    state.selectionHandler = () => {
        const selectionStart = textareaElement.selectionStart;
        const selectionEnd = textareaElement.selectionEnd;
        state.notifyCursorPosition(selectionStart, selectionEnd);
    };

    // Add event listeners
    textareaElement.addEventListener('scroll', state.scrollHandler, { passive: true });
    textareaElement.addEventListener('input', state.inputHandler);
    textareaElement.addEventListener('blur', state.blurHandler);
    textareaElement.addEventListener('selectionchange', state.selectionHandler);
    textareaElement.addEventListener('click', state.selectionHandler);
    textareaElement.addEventListener('keyup', state.selectionHandler);

    // Configure Prism to show line numbers
    preElement.classList.add('line-numbers');

    // Store the instance state for later reference
    instances.set(textareaElement, state);

    // Do initial update if there's existing content
    if (textareaElement.value) {
        updateContent(textareaElement.value, state);
    }
}

/**
 * Sets the syntax highlighting theme (light or dark).
 * 
 * @param {boolean} isDark - Whether to use the dark theme
 * @export
 */
export function setTheme(isDark) {
    // Remove existing Prism theme if any
    if (currentThemeLink) {
        currentThemeLink.remove();
    }

    // Create new link element for the theme stylesheet
    const link = document.createElement('link');
    link.rel = 'stylesheet';
    link.href = isDark ? '/_content/Refund/css/prism-dark.css' : '/_content/Refund/css/prism-light.css';

    // Add to document head
    document.head.appendChild(link);
    currentThemeLink = link;

    // Re-highlight all code editors to apply the new theme
    const editors = document.querySelectorAll('.code-editor pre code');
    editors.forEach(codeElement => {
        if (isPrismAvailable()) {
            window.Prism.highlightElement(codeElement);
        }
    });
}

/**
 * Initializes a new code editor instance.
 * 
 * This is the main entry point called from the Blazor component.
 * It sets up the editor, configures syntax highlighting, and applies the theme.
 * 
 * @param {HTMLTextAreaElement} textareaElement - The textarea element for capturing input
 * @param {HTMLElement} preElement - The pre element for displaying highlighted code
 * @param {HTMLElement} codeElement - The code element within the pre element
 * @param {DotNetReference} dotNetRef - Reference to .NET component for callbacks
 * @param {boolean} isDark - Whether to use the dark theme
 * @param {string} initialValue - Initial content value
 * @export
 */
export function initialize(textareaElement, preElement, codeElement, dotNetRef, isDark, initialValue) {
    if (isPrismAvailable()) {
        setupCustomGrammar();
    }

    createInstance(textareaElement, preElement, codeElement, dotNetRef);

    // Set initial value if provided
    if (initialValue) {
        textareaElement.value = initialValue;
        const state = instances.get(textareaElement);
        if (state) {
            updateContent(initialValue, state);
        }
    }

    // Ensure theme is set
    if (!currentThemeLink) {
        setTheme(isDark);
    }
}

/**
 * Updates the editor's content programmatically.
 * 
 * Called from the Blazor component when the Value property changes.
 * Preserves cursor position during updates to prevent cursor jumping.
 * 
 * @param {HTMLTextAreaElement} textareaElement - The textarea element reference
 * @param {string} value - The new content value to set
 * @export
 */
export function setValue(textareaElement, value) {
    const state = instances.get(textareaElement);
    if (!state) return;

    // Only update if value actually changed (prevents unnecessary redraws)
    if (textareaElement.value !== value) {
        // Always preserve cursor position during programmatic updates
        const selectionStart = textareaElement.selectionStart;
        const selectionEnd = textareaElement.selectionEnd;
        
        textareaElement.value = value;
        updateContent(value, state);
        
        // Restore cursor position, clamping to new content length
        const maxPos = value.length;
        textareaElement.setSelectionRange(
            Math.min(selectionStart, maxPos),
            Math.min(selectionEnd, maxPos)
        );
    }
}

/**
 * Gets the current cursor position in the editor.
 * 
 * @param {HTMLTextAreaElement} textareaElement - The textarea element reference
 * @returns {object} Object with selectionStart and selectionEnd properties
 * @export
 */
export function getCursorPosition(textareaElement) {
    return {
        selectionStart: textareaElement.selectionStart,
        selectionEnd: textareaElement.selectionEnd
    };
}

/**
 * Sets the cursor position in the editor.
 * 
 * @param {HTMLTextAreaElement} textareaElement - The textarea element reference
 * @param {number} selectionStart - The start position of the selection
 * @param {number} selectionEnd - The end position of the selection (optional, defaults to selectionStart)
 * @export
 */
export function setCursorPosition(textareaElement, selectionStart, selectionEnd = null) {
    const endPos = selectionEnd !== null ? selectionEnd : selectionStart;
    const maxPos = textareaElement.value.length;
    
    // Clamp positions to valid range
    const clampedStart = Math.max(0, Math.min(selectionStart, maxPos));
    const clampedEnd = Math.max(0, Math.min(endPos, maxPos));
    
    textareaElement.setSelectionRange(clampedStart, clampedEnd);
    textareaElement.focus();
}

/**
 * Performs cleanup when the editor component is disposed.
 * 
 * Removes event listeners and clears instance state to prevent memory leaks.
 * 
 * @param {HTMLTextAreaElement} textareaElement - The textarea element reference
 * @export
 */
export function cleanup(textareaElement) {
    const state = instances.get(textareaElement);
    if (!state) return;

    // Remove event listeners
    if (state.scrollHandler) {
        textareaElement.removeEventListener('scroll', state.scrollHandler);
    }
    if (state.inputHandler) {
        textareaElement.removeEventListener('input', state.inputHandler);
    }
    if (state.blurHandler) {
        textareaElement.removeEventListener('blur', state.blurHandler);
    }
    if (state.selectionHandler) {
        textareaElement.removeEventListener('selectionchange', state.selectionHandler);
        textareaElement.removeEventListener('click', state.selectionHandler);
        textareaElement.removeEventListener('keyup', state.selectionHandler);
    }

    // Remove the instance from tracking map
    instances.delete(textareaElement);
}
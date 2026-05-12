export function initialize(dotNetRef, elementSelector) {
    let isInitialClick = true;

    const handleClickOutside = (event) => {
        // On the first click (opening click), just reset the flag and return
        if (isInitialClick) {
            isInitialClick = false;
            return;
        }

        // Check if the clicked element or any of its ancestors match the selector
        let targetElement = event.target;
        let isInside = false;

        // Also check if the click is on the anchor element
        const anchorElement = document.getElementById(event.target.closest('[id]')?.id);
        if (anchorElement && anchorElement.id === event.target.id) {
            return;
        }

        while (targetElement && !isInside) {
            if (targetElement.matches && targetElement.matches(elementSelector)) {
                isInside = true;
            }
            targetElement = targetElement.parentElement;
        }

        if (!isInside) {
            dotNetRef.invokeMethodAsync('HandleClickOutside');
            isInitialClick = true; // Reset for next time menu opens
        }
    };

    // Add click listener to document
    document.addEventListener('click', handleClickOutside);

    // Return a function to remove the event listener
    return {
        dispose: () => {
            document.removeEventListener('click', handleClickOutside);
        }
    };
}
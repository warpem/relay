function addClickOutsideEvent(elementId, dotNetHelper) {
    window.addEventListener('click', function (e) {
        const element = document.getElementById(elementId);
        if (element && !element.contains(e.target)) {
            dotNetHelper.invokeMethodAsync('HandleClickOutside');
        }
    });
}

export {
    addClickOutsideEvent
}
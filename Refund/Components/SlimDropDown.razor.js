var dotNetRegistrations = {};
var instances = {};

export function registerComponent(dotnetReference, id) {
    dotNetRegistrations[id] = dotnetReference;
    instances[id] = {
        dotNetRegistrations: dotnetReference,
        id: id,
    }

    $("#skinny-drop-down-" + id)
        .on('focusout', function (event) { hidePopup(id) });
}

export function hidePopup(id) {
    $("#skinny-drop-down-" + id + " .popup").hide();
}

export function showPopup(id, e) {
    $("#skinny-drop-down-" + id + " .popup").show();
    $("#skinny-drop-down-" + id).get(0).focus();
}
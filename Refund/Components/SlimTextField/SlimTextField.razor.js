var dotNetRegistrations = {};
var instances = {}

export function registerComponent(dotnetReference, id, step, min, max, debounce) {
    dotNetRegistrations[id] = dotnetReference;
    instances[id] = {
        dotNetRegistrations: dotnetReference,
        id: id,
        step: step,
        min: min,
        max: max,
        debounce: debounce
    }
    instances[id].debounces = null;
    $("#slim-text-field-" + id)
        .on('input', function (event) { updateField(this, event, id); })
        .on('keydown', function (event) { return keyPress(this, event, id); })
        .on('wheel', function (event) { return mouseWheel(this, event, id); });
}

function keyPress(obj, event, id) {
    var result = true;
    if (event.keyCode == 38) { // Up arrow
        incrementValue(obj, id);
        result = false;
    } else if (event.keyCode == 40) { // Down arrow
        decrementValue(obj, id);
        result = false;
    } else if (event.keyCode == 13) { // Enter key
        // Commit changes
        filterNumber(obj);
        obj.blur();
        var sel = window.getSelection();
        sel.removeAllRanges();
        commit(obj, id);
        result = false;
    }

    return result;
}

function mouseWheel(obj, event, id) {
    event.preventDefault(); // Prevent page scrolling
    var delta = event.originalEvent.deltaY;
    if (delta < 0) {
        // Scrolled up, increment value
        incrementValue(obj, id);
    } else {
        // Scrolled down, decrement value
        decrementValue(obj, id);
    }
    return false;
}

function incrementValue(obj, id) {
    var step = $(obj).attr("step");
    if (step) {
        step = Number(step);
        var max = $(obj).attr("max");
        var value = +(Number(obj.innerHTML) + step).toPrecision(12);
        if (max) {
            max = Number(max);
            if (value <= max) {
                obj.innerHTML = value;
            } else {
                obj.innerHTML = max;
            }
        } else {
            obj.innerHTML = value;
        }
        selectAllText(obj);
        updateField(obj, null, id);
        window.getSelection().removeAllRanges();
    }
}

function decrementValue(obj, id) {
    var step = $(obj).attr("step");
    if (step) {
        step = Number(step);
        var min = $(obj).attr("min");
        var value = +(Number(obj.innerHTML) - step).toPrecision(12);
        if (min) {
            min = Number(min);
            if (value >= min) {
                obj.innerHTML = value;
            } else {
                obj.innerHTML = min;
            }
        } else {
            obj.innerHTML = value;
        }
        selectAllText(obj);
        updateField(obj, null, id);
        window.getSelection().removeAllRanges();
    }
}

function selectAllText(obj) {
    var sel = window.getSelection();
    var range = document.createRange();
    range.setStart(obj.firstChild, 0);
    range.setEnd(obj.firstChild, obj.innerHTML.length);
    sel.removeAllRanges();
    sel.addRange(range);
}

function updateField(obj, event, id) {
    filterNumber(obj);

    var value = Number(obj.innerHTML);
    var min = $(obj).attr("min");
    min = min === "" ? NaN : Number(min);
    var max = $(obj).attr("max");
    max = max === "" ? NaN : Number(max);

    if ((!isNaN(min) && value < min) || (!isNaN(max) && value > max)) {
        $(obj).addClass("invalid");
        instances[id].debounces = null; // no server updates if invalid
    } else {
        $(obj).removeClass("invalid");
        commitWithDebounce(obj, id);
    }
}

function filterNumber(obj) {
    var sel = window.getSelection();
    var range = sel.getRangeAt(0);
    if (obj.innerHTML.search(/^[-+]?[\d]*[.]?[\d]*$/)) {
        // Remove non-digit characters
        var html = obj.innerHTML.replace(/[^\d]/g, '');

        // Re-insert decimal if present
        var decimalIndex = obj.innerHTML.indexOf('.');
        if (decimalIndex != -1) {
            var nonDigitsMatches = obj.innerHTML.slice(0, decimalIndex).match(/[^\d]/g);
            var nonDigits = nonDigitsMatches === null ? 0 : nonDigitsMatches.length;
            html = html.slice(0, decimalIndex - nonDigits) + '.' + html.slice(decimalIndex - nonDigits);
        }

        // Negative sign support
        var isNegative = obj.innerHTML.charAt(0) === '-';
        if (isNegative) {
            html = '-' + html;
        }

        var start = range.startOffset;
        obj.innerHTML = html;
        range = document.createRange();
        range.setStart(obj.firstChild, Math.max(0, start - 1));
        range.collapse(true);

        sel.removeAllRanges();
        sel.addRange(range);
    }
}

function commitWithDebounce(obj, id) {
    if (instances[id].debounces !== null) {
        clearTimeout(instances[id].debounces);
    }

    if (instances[id].debounce) {
        instances[id].debounces = setTimeout(function () {
            commit(obj, id);
        }, instances[id].debounce);
    }
}

function commit(obj, id) {
    instances[id].debounces = null;
    var value = obj.innerHTML;
    dotNetRegistrations[id].invokeMethodAsync('UpdateFilterValue', value);
}

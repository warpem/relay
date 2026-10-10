// Browser-side interaction keeps hover, scrolling and keyboard navigation independent
// of Blazor Server latency. Popovers retain their DOM ancestry but escape clipping.
const controllers = new Set();
let active = null;
let globalEvents = null;
const margin = 8;
const items = surface => [...surface.querySelector('.relay-menu-scroll').children]
    .filter(child => child.classList.contains('relay-menu-entry'))
    .map(entry => entry.querySelector('.relay-menu-item'));
const submenu = item => item?.getAttribute('aria-controls')
    ? document.getElementById(item.getAttribute('aria-controls')) : null;
const clamp = (value, min, max) => Math.max(min, Math.min(value, Math.max(min, max)));

function anchorController(target) {
    for (let element = target; element instanceof Element; element = element.parentElement) {
        for (const controller of controllers) {
            if (controller.options.context && element.id && element.id === controller.options.anchor)
                return controller;
        }
    }
    return null;
}

function installGlobalEvents() {
    if (globalEvents) return;
    globalEvents = new AbortController();
    const capture = { capture: true, signal: globalEvents.signal };
    document.addEventListener('contextmenu', event => {
        const controller = anchorController(event.target);
        if (controller) {
            event.preventDefault();
            event.stopPropagation();
            controller.requestOpen(event.clientX, event.clientY, event.target);
        } else if (active?.root.contains(event.target)) {
            event.preventDefault();
        } else {
            active?.close(true, false);
        }
    }, capture);
    document.addEventListener('pointerdown', event => {
        if (!active || active.root.contains(event.target)) return;
        // A button-driven menu owns its toggle; closing here would race the button's
        // click callback and turn a close into a reopen.
        const anchor = document.getElementById(active.options.anchor);
        if (event.button === 0 && !active.options.context && anchor?.contains(event.target)) return;
        active.close(true, false);
    }, capture);
    document.addEventListener('keydown', event => {
        if (event.key === 'ContextMenu' || (event.shiftKey && event.key === 'F10')) {
            const controller = anchorController(event.target);
            if (controller) {
                event.preventDefault();
                event.stopPropagation();
                const rect = document.getElementById(controller.options.anchor).getBoundingClientRect();
                controller.requestOpen(rect.left, rect.bottom, event.target);
                return;
            }
            const target = event.target.closest('[data-relay-context="true"]');
            if (target) {
                event.preventDefault();
                event.stopPropagation();
                const rect = target.getBoundingClientRect();
                target.dispatchEvent(new MouseEvent('contextmenu', {
                    bubbles: true, cancelable: true, button: 2,
                    clientX: rect.left + 8, clientY: rect.top + Math.min(24, rect.height)
                }));
                return;
            }
        }
        active?.keydown(event);
    }, capture);
    document.addEventListener('pointermove', event => active?.pointermove(event), capture);
    document.addEventListener('click', event => active?.click(event), capture);
    window.addEventListener('blur', event => {
        if (event.target === window) active?.close(true, false);
    }, capture);
    window.addEventListener('resize', () => active?.scheduleLayout(), capture);
    window.visualViewport?.addEventListener('resize', () => active?.scheduleLayout(), capture);
    window.visualViewport?.addEventListener('scroll', () => active?.scheduleLayout(), capture);
    document.addEventListener('scroll', event => {
        if (active && !active.root.contains(event.target)) active.close(true, false);
    }, capture);
}

export function initialize(id, dotnet) {
    const root = document.getElementById(id);
    // A navigation can remove the component while its JS module is loading.
    if (!root) return null;
    let chain = [];
    let point = null;
    let returnFocus = null;
    let hoverTimer, scrollTimer, layoutFrame;
    let hovered = null;
    let previousPointer = null;
    let typeBuffer = '', typeTime = 0;
    let disposed = false;
    let contextRequested = false;
    let pendingAction = false;
    const events = new AbortController();
    const observer = new ResizeObserver(() => controller.scheduleLayout());
    const mutations = new MutationObserver(() => {
        if (!root.isConnected) return;
        controller.scheduleLayout();
    });
    mutations.observe(root, { childList: true, subtree: true, characterData: true });
    observer.observe(root);

    function notify(value) {
        if (!disposed) dotnet.invokeMethodAsync('SetOpen', value).catch(error => {
            if (!disposed) console.error('Relay menu state update failed', error);
        });
    }
    function cancelHover() { clearTimeout(hoverTimer); hovered = null; }
    function stopScroll() { clearInterval(scrollTimer); scrollTimer = null; }
    function hideAfter(index) {
        const focusRemoved = index >= 0 && chain.slice(index + 1).some(surface => surface.contains(document.activeElement));
        while (chain.length > index + 1) {
            const surface = chain.pop();
            if (hovered?.closest('.relay-menu-surface') === surface) stopScroll();
            const parent = surface === root ? null : surface.parentElement?.querySelector('.relay-menu-item');
            parent?.setAttribute('aria-expanded', 'false');
            surface.querySelectorAll('[data-active]').forEach(item => item.removeAttribute('data-active'));
            if (surface.matches(':popover-open')) surface.hidePopover();
            observer.unobserve(surface);
        }
        if (focusRemoved) chain[index]?.querySelector('.relay-menu-scroll').focus({ preventScroll: true });
    }
    function focusItem(item) {
        if (!item) return;
        const surface = item.closest('.relay-menu-surface');
        for (const sibling of items(surface)) sibling.toggleAttribute('data-active', sibling === item);
        item.focus({ preventScroll: true });
        // scrollIntoView also scrolls ancestors of a top-layer popover. Move only
        // this menu's viewport, never the diagram/page behind it.
        const scroll = surface.querySelector('.relay-menu-scroll');
        const row = item.getBoundingClientRect(), viewport = scroll.getBoundingClientRect();
        if (row.top < viewport.top) scroll.scrollTop -= viewport.top - row.top;
        else if (row.bottom > viewport.bottom) scroll.scrollTop += row.bottom - viewport.bottom;
        updateArrows(surface);
    }
    function updateArrows(surface) {
        const scroll = surface.querySelector('.relay-menu-scroll');
        const arrows = [...surface.children].filter(el => el.matches('[data-scroll]'));
        // Keep both strips allocated while overflowing so rows don't jump at the ends.
        const overflowing = scroll.scrollHeight > surface.clientHeight - 6 + 1;
        arrows.forEach(arrow => { arrow.hidden = !overflowing; });
        arrows[0].disabled = scroll.scrollTop <= 1;
        arrows[1].disabled = scroll.scrollTop + scroll.clientHeight >= scroll.scrollHeight - 1;
    }
    function bounds() {
        const viewport = window.visualViewport;
        const left = viewport?.offsetLeft ?? 0, top = viewport?.offsetTop ?? 0;
        return { left: left + margin, top: top + margin,
            right: left + (viewport?.width ?? window.innerWidth) - margin,
            bottom: top + (viewport?.height ?? window.innerHeight) - margin };
    }
    function position(surface, parentItem = null) {
        const available = bounds();
        const anchor = !parentItem && !point && controller.options.y == null
            ? document.getElementById(controller.options.anchor)?.getBoundingClientRect() : null;
        let maxHeight = Math.min(480, available.bottom - available.top);
        let above = false;
        if (anchor) {
            const belowSpace = available.bottom - anchor.bottom;
            const aboveSpace = anchor.top - available.top;
            const naturalHeight = surface.querySelector('.relay-menu-scroll').scrollHeight + 8;
            above = belowSpace < Math.min(maxHeight, naturalHeight) && aboveSpace > belowSpace;
            maxHeight = Math.min(maxHeight, Math.max(60, above ? aboveSpace : belowSpace));
        }
        surface.style.maxHeight = `${maxHeight}px`;
        surface.style.maxWidth = `${available.right - available.left}px`;
        updateArrows(surface);
        const rect = surface.getBoundingClientRect();
        let x, y;
        if (parentItem) {
            const parent = parentItem.closest('.relay-menu-surface').getBoundingClientRect();
            const itemRect = parentItem.getBoundingClientRect();
            const first = items(surface)[0];
            const firstOffset = first ? first.getBoundingClientRect().top - rect.top + surface.querySelector('.relay-menu-scroll').scrollTop : 4;
            y = itemRect.top - firstOffset;
            const preferLeft = parentItem.closest('.relay-menu-surface').dataset.side === 'left';
            const fitsRight = parent.right - 1 + rect.width <= available.right;
            const fitsLeft = parent.left + 1 - rect.width >= available.left;
            const left = preferLeft ? fitsLeft || !fitsRight : !fitsRight && fitsLeft;
            x = left ? parent.left + 1 - rect.width : parent.right - 1;
            surface.dataset.side = left ? 'left' : 'right';
        } else {
            x = point?.x ?? controller.options.x ?? anchor?.left ?? available.left;
            y = point?.y ?? controller.options.y ?? anchor?.bottom ?? available.top;
            if (anchor && above) y = anchor.top - rect.height;
            surface.dataset.side = 'right';
        }
        surface.style.left = `${clamp(x, available.left, available.right - rect.width)}px`;
        surface.style.top = `${clamp(y, available.top, available.bottom - rect.height)}px`;
    }
    function openSubmenu(item, keyboard = false) {
        const surface = item.closest('.relay-menu-surface');
        const index = chain.indexOf(surface);
        const child = submenu(item);
        if (index < 0) return;
        if (child && chain[index + 1] === child) {
            if (keyboard) focusItem(items(child)[0]);
            return;
        }
        hideAfter(index);
        for (const sibling of items(surface)) sibling.toggleAttribute('data-active', sibling === item);
        if (!child || item.getAttribute('aria-disabled') === 'true') return;
        child.querySelector('.relay-menu-scroll').scrollTop = 0;
        child.showPopover();
        chain.push(child);
        item.setAttribute('aria-expanded', 'true');
        observer.observe(child);
        position(child, item);
        if (keyboard) focusItem(items(child)[0]);
    }
    function scrollByArrow(arrow) {
        if (arrow.disabled) { stopScroll(); return; }
        const surface = arrow.closest('.relay-menu-surface');
        hideAfter(chain.indexOf(surface));
        surface.querySelector('.relay-menu-scroll').scrollTop += Number(arrow.dataset.scroll) * 24;
        updateArrows(surface);
    }
    root.addEventListener('scroll', event => {
        if (!event.target.classList?.contains('relay-menu-scroll')) return;
        const surface = event.target.closest('.relay-menu-surface');
        const index = chain.indexOf(surface);
        const child = chain[index + 1];
        if (child) {
            const parent = child.parentElement.querySelector('.relay-menu-item');
            const rect = parent.getBoundingClientRect(), view = event.target.getBoundingClientRect();
            if (rect.top < view.top || rect.bottom > view.bottom) hideAfter(index);
            else position(child, parent);
        }
        updateArrows(surface);
    }, { capture: true, signal: events.signal });
    root.addEventListener('wheel', event => {
        const surface = event.target.closest('.relay-menu-surface');
        if (!surface) return;
        event.preventDefault();
        const scroll = surface.querySelector('.relay-menu-scroll');
        scroll.scrollTop += event.deltaY * (event.deltaMode === 1 ? 24 : event.deltaMode === 2 ? scroll.clientHeight : 1);
    }, { passive: false, signal: events.signal });

    const controller = {
        root, options: {},
        requestOpen(x, y, target) {
            active?.close(true, false);
            point = { x, y };
            returnFocus = target.closest('a,button,[tabindex]') ?? document.getElementById(this.options.anchor);
            contextRequested = true;
            notify(true);
        },
        update(open, options) {
            this.options = options;
            if (!open) { pendingAction = false; this.close(false, false); return; }
            if (pendingAction) return;
            if (!root.isConnected) return;
            if (active !== this) {
                active?.close(true, false);
                active = this;
                if (!contextRequested) {
                    point = null;
                    returnFocus = document.activeElement;
                }
                contextRequested = false;
                root.showPopover();
                observer.observe(root);
                root.querySelector('.relay-menu-scroll').scrollTop = 0;
                chain = [root];
                position(root);
                focusItem(items(root)[0]);
                if (!items(root).length) root.querySelector('.relay-menu-scroll').focus({ preventScroll: true });
            }
            this.scheduleLayout();
        },
        close(inform = true, restore = true) {
            const wasOpen = chain.length > 0;
            cancelHover(); stopScroll();
            cancelAnimationFrame(layoutFrame);
            hideAfter(-1);
            if (active === this) active = null;
            if (wasOpen && restore && returnFocus?.isConnected) returnFocus.focus({ preventScroll: true });
            if (inform) notify(false);
        },
        scheduleLayout() {
            cancelAnimationFrame(layoutFrame);
            if (active !== this) return;
            layoutFrame = requestAnimationFrame(() => {
                for (let i = 0; i < chain.length; i++) {
                    const surface = chain[i];
                    if (!surface.isConnected) { hideAfter(i - 1); break; }
                    position(surface, i ? surface.parentElement.querySelector('.relay-menu-item') : null);
                }
            });
        },
        pointermove(event) {
            if (event.pointerType === 'touch') return;
            const previous = previousPointer;
            previousPointer = { x: event.clientX, y: event.clientY };
            const arrow = event.target.closest('[data-scroll]');
            if (arrow && root.contains(arrow)) {
                if (hovered === arrow) return;
                cancelHover(); stopScroll(); hovered = arrow;
                if (arrow.disabled) return;
                scrollByArrow(arrow);
                scrollTimer = setInterval(() => scrollByArrow(arrow), 70);
                return;
            }
            stopScroll();
            const item = event.target.closest('.relay-menu-item');
            if (!item || !root.contains(item)) { cancelHover(); return; }
            if (item === hovered) return;
            cancelHover(); hovered = item;
            const surface = item.closest('.relay-menu-surface');
            const child = chain[chain.indexOf(surface) + 1];
            // Give diagonal travel toward the open submenu a longer grace period.
            let delay = 160;
            if (child && previous) {
                const rect = child.getBoundingClientRect();
                const edge = child.dataset.side === 'left' ? rect.right : rect.left;
                const fraction = (event.clientX - previous.x) / (edge - previous.x);
                const top = previous.y + (rect.top - 8 - previous.y) * fraction;
                const bottom = previous.y + (rect.bottom + 8 - previous.y) * fraction;
                if (fraction > 0 && fraction < 1 && event.clientY >= top && event.clientY <= bottom) delay = 400;
            }
            // Highlight immediately; only submenu changes need a navigation grace period.
            focusItem(item);
            hoverTimer = setTimeout(() => {
                if (active === this && item.isConnected) {
                    openSubmenu(item);
                }
            }, delay);
        },
        click(event) {
            if (!root.contains(event.target)) return;
            const arrow = event.target.closest('[data-scroll]');
            if (arrow) { event.preventDefault(); scrollByArrow(arrow); return; }
            const item = event.target.closest('.relay-menu-item');
            if (!item) return;
            if (item.getAttribute('aria-disabled') === 'true') { event.preventDefault(); return; }
            cancelHover();
            if (submenu(item)) { openSubmenu(item, true); return; }
            // Hide immediately; preserve the server-side command context until its
            // callback completes. An unrelated render must not reopen this surface.
            pendingAction = true;
            this.close(false, true);
        },
        keydown(event) {
            const surface = event.target.closest('.relay-menu-surface') ?? chain.at(-1);
            if (!surface) return;
            const list = items(surface);
            const focused = event.target.closest('.relay-menu-item');
            const index = list.indexOf(focused);
            let next = null;
            switch (event.key) {
                case 'ArrowDown': next = list[(index + 1) % list.length]; break;
                case 'ArrowUp': next = list[index <= 0 ? list.length - 1 : index - 1]; break;
                case 'Home': next = list[0]; break;
                case 'End': next = list.at(-1); break;
                case 'PageDown':
                case 'PageUp': {
                    const step = Math.max(1, Math.floor(surface.querySelector('.relay-menu-scroll').clientHeight / (focused?.offsetHeight || 30)) - 1);
                    next = list[clamp(index + (event.key === 'PageDown' ? step : -step), 0, list.length - 1)];
                    break;
                }
                case 'ArrowRight': if (focused) openSubmenu(focused, true); break;
                case 'ArrowLeft':
                case 'Escape': {
                    const level = chain.indexOf(surface);
                    if (level > 0) {
                        const parent = surface.parentElement.querySelector('.relay-menu-item');
                        hideAfter(level - 1); focusItem(parent);
                    } else if (event.key === 'Escape') this.close(true, true);
                    break;
                }
                case 'Enter':
                case ' ': if (focused) focused.click(); break;
                case 'Tab': this.close(true, true); return;
                default: {
                    if (event.key.length !== 1 || event.ctrlKey || event.metaKey || event.altKey) return;
                    const now = performance.now();
                    typeBuffer = now - typeTime > 700 ? event.key : typeBuffer + event.key;
                    typeTime = now;
                    const query = [...typeBuffer].every(c => c === typeBuffer[0]) ? typeBuffer[0] : typeBuffer;
                    const ordered = [...list.slice(index + 1), ...list.slice(0, index + 1)];
                    next = ordered.find(item => item.querySelector('.relay-menu-label').textContent.trim().toLocaleLowerCase().startsWith(query.toLocaleLowerCase()));
                }
            }
            cancelHover();
            if (next) { hideAfter(chain.indexOf(surface)); focusItem(next); }
            event.preventDefault(); event.stopPropagation();
        },
        dispose() {
            this.close(false, false);
            disposed = true;
            events.abort(); observer.disconnect(); mutations.disconnect();
            controllers.delete(this);
            if (!controllers.size) { globalEvents?.abort(); globalEvents = null; }
        }
    };
    controllers.add(controller);
    installGlobalEvents();
    return controller;
}

# Relay menu browser regression harness

Runs the actual Relay Razor components and browser module with synthetic commands;
no projects, users, jobs, or scientific data are loaded or modified.

```sh
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5002 \
  dotnet run --project tests/Relay.MenuHarness
```

Open http://localhost:5002. The harness compiles linked Razor sources and serves
JavaScript/CSS directly from Relay. Reload after JS/CSS changes; rebuild for Razor changes.

Check these behaviors in a browser:

- Right-click each target, including the bottom/right edges and the clipped,
  transformed container. All levels stay within an 8px viewport margin and above
  the maximum-z-index overlay. Repeat at 640×360 and with browser zoom.
- A long menu has up/down scroll strips, indicating only available directions.
  Wheel/trackpad, clicking a strip, and hovering continuously over a strip reach
  all 60 entries. The page behind it does not scroll.
- Nested menu → Deep menu opens left at the right edge. Adjacent surfaces touch,
  and the first child row aligns with the parent unless the viewport forces a shift.
  Move diagonally into an open submenu; crossing neighboring rows briefly must
  not replace it. Scrolling its parent row out of view closes the child.
- Shift+F10 and the context-menu key open at the focused target. Arrow keys,
  Home/End, Page Up/Down, and typing a label move focus and reveal the item.
  Escape/Left returns to the parent; Escape at the root restores target focus;
  Tab closes the menu and continues traversal.
- "Unavailable command" remains focusable with its explanation, but Enter,
  Space, or clicking it must not change the execution counter.
- Activating any enabled command closes the whole chain and increments the
  counter exactly once. Try keyboard Enter/Space and clicking deep items.
- Clicking the outside target (which deliberately has an ID) closes the menu.
  Right-clicking a different target replaces it. Repeated toggle-button clicks
  alternate open/closed, with the button remaining outside its own menu.
- Switching browser windows dismisses the menu. Resizing repositions it.

Also verify integration in Relay: list and diagram job-card menus, background job
creation, port-connection menus, listing cards, queue cards, and action-panel
buttons. Destructive actions retain the “I'm sure” → “Yes, I'm really sure”
confirmation submenus; dismissing either level must leave the model unchanged.
Unavailable actions show an explanation submenu, with only their labels struck
through (not icons or chevrons).

# Fluent UI v5 browser checks

Build the solution, then run the opt-in browser checks:

```sh
dotnet build Relay.sln
cd tests/Relay.FluentMigration
npm install
npx playwright install chromium
npm test
```

Side-callout positioning tests run without a browser:

```sh
node --test tests/Relay.FluentMigration/popover-positioning.test.mjs
node --test tests/Relay.FluentMigration/tooltip-fallback.test.mjs
```

The harness starts ComponentDev on port 5131 and a TCP proxy on port 5132. The
proxy delays traffic by 150 ms in each direction, including WebSocket traffic.
The verification page re-renders its parent every 25 ms while inputs are edited.
The server, proxy, and isolated browser are stopped when the test finishes.
Screenshots and server logs are saved under the system temporary directory in
`relay-fluent-migration` (override with `FLUENT_TEST_ARTIFACTS`).

The checks cover immediate text entry and caret editing, the job string wrapper,
slider keyboard updates, disabled fractional numbers, shared listbox selection,
confirmation popovers, dialog results, notifications, theme switching, and tabs.
They also reject browser exceptions. They do not cover every application screen
or prove that slider dragging and concurrent external updates are race-free.

Use `CHROMIUM_EXECUTABLE` to select an already installed Chrome/Chromium instead
of the Playwright browser download. `PLAYWRIGHT_MODULE` optionally selects an
existing Playwright module installation.

## Input binding regression checks

After building, run `npm run test:bindings` from this directory. This uses ports
5141/5142, the same 300 ms network round trip, a parent render every 25 ms, and
400 ms asynchronous model updates. It covers initially null text, cursor editing,
standalone text inputs and textareas, int2/int3 step buttons while focused,
preserving sibling coordinates, rejected updates, resets, and switching entities.
Numeric arithmetic tests also cover decimal precision, large integers, bounds,
partial input, scientific notation, and localized number formats.

For a visual check of navigation and action listboxes, open ComponentDev's
`/listbox-layout` page. It contains a 200px filtered project selector and a 150px
queue menu. Check start alignment, vertical centering, ellipsis for long labels,
filtering, and selection. These menus omit the selection-marker slot; ordinary
Fluent selection inputs retain their checkmarks.

For job parameter accordion hit areas, open `/accordion-interaction`. Editing a
field and clicking the gutter beside it or the space between fields must blur
the field without collapsing the section. Header text, chevrons, Enter, and
Space must still toggle it; field buttons and checkboxes must still work.
The shared native capture guard rejects only clicks retargeted to the job
accordion host outside its header, before Fluent's item listener runs.

## Firefox tooltip fallback

Relay and ComponentDev register a Fluent 5.0.1 workaround before Blazor.
Browsers with native CSS anchors retain Fluent's implementation. Other browsers
bypass its unbounded `showTooltip` promise recursion and generated CSS, using
Relay's shared popover placement policy and the actual DOM anchor instead.
Positioning follows scroll and resize while visible. Failed asset loading
suppresses the tooltip; hiding or removal cancels delayed shows and cleans up
observers. Tests cover placement, loading failure, numeric GUID anchors, rapid
requests, scrolling, and cancellation. Verify an actual Firefox session by
repeatedly opening job editors and hovering/focusing field labels.

The job views await binding callbacks and retain a whole local draft while a
commit is pending. Superseded queued assignments are skipped. Field keys follow
job/property identity. The shared text adapters normalize null to empty text so
Fluent's focused-value snapshot has a stable initial value. Numeric step buttons
edit the live browser text and send native input/change events through Fluent's
mask, binding, and validation, avoiding its focused display freeze for server-side
step buttons.

## Remaining upstream input issue

The default checks use v5's 200 ms browser debounce. Short or zero delays can
still corrupt text under this stress. Reproduce the zero-delay case with:

```sh
FLUENT_TEST_DELAY=0 npm test
```

That diagnostic is expected to fail the text assertion with Fluent UI 5.0.1;
it is not part of the default passing checks. The migration improves ordinary
editing but does not establish an acknowledgement protocol for all browser and
server value updates. Related upstream reports:

- https://github.com/microsoft/fluentui-blazor/issues/5137
- https://github.com/microsoft/fluentui-blazor/issues/5250

## Beam classification layout

`npm run test:beam-layout` runs an isolated browser check using the classification
CSS, viewport markup, and actual isosurface viewer module. It checks eight class
cards at multiple window sizes and device pixel ratios, including stable canvas
and document dimensions. It does not need a Relay server or particle data.
`PLAYWRIGHT_MODULE` and `CHROMIUM_EXECUTABLE` can select existing installations.


`npm run test:class2d-mrc` exercises the shared MRC worker and image tiles with
150 classes, float32/float16/int16 stacks, signed contrast, vertical orientation,
shared fetches, sorting, error messages and disposal. It accepts the same
`PLAYWRIGHT_MODULE` / `CHROMIUM_EXECUTABLE` overrides as the layout test;
`CLASS2D_TEST_STACK` optionally adds a real 150-class stack to the run.

The class renderer check also covers browser sorting, keyboard and bulk selection,
contrast handles, lazy subclass panels, combined export ID remapping and disposal.

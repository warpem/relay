# Error boundary proposal (2026-11-16)

Audit of all 200 Razor components for places where one failing component kills the whole
Blazor circuit. Five read-only audits (shell, main screens, overlays/right panels, shared Refund
components, job-specific views), merged and spot-checked against the code. Items marked ✓ were
re-verified by hand.

Today only two boundaries exist: `ExpandedJobView.razor:18` and `FileBrowserDialog.razor:7`.

## How ErrorBoundary behaves here (constraints on placement)

- Catches render, lifecycle and event-handler exceptions of **descendants only**. A boundary inside a
  component's own `.razor` does not catch that component's own `OnParametersSet` or render, so it
  has to be placed at the parent or host.
- It does **not** catch `async void`, fire-and-forget `_ = FooAsync()`, timer callbacks, or the
  GroupEvent handlers. Those handlers run on thread-pool threads, and `GroupEvent.cs:186` logs and
  swallows their exceptions. A render requested via `InvokeAsync(StateHasChanged)` that throws *is*
  routed to the nearest boundary.
- Event-callback exceptions go to the boundary of the component that **received the event**, not to
  the component whose code threw. Example: a JobCard double-click that fails inside ViewScreen
  navigation blanks the card.
- Dialog content renders under `<FluentProviders/>` (`MainLayout.razor:113`), outside every layout
  region. Each dialog body needs its own boundary. Do not wrap FluentProviders itself, or a
  failure takes the toasts down with it.
- A tripped boundary stays tripped until `Recover()` is called. Components recreated by `Recover()`
  must unsubscribe correctly in `Dispose`, or GroupEvent subscribers pile up.

## Step 0: prerequisites

1. ✓ **Make the fatal-error bar visible.** `#blazor-error-ui` (`App.razor:35`) has no CSS anywhere,
   and `.layout-container` is `position: fixed` full-viewport. A dead circuit looks like a frozen
   page. Give it fixed, bottom, high z-index styling.
2. **Add `Refund/Components/RelayErrorBoundary : ErrorBoundary`.** All three audits that touched
   the topic proposed the same component:
   - Parameters: `Region` (label), `ResetKey` (object; `OnParametersSet` calls `Recover()` when it
     changes), optional `ResetOnNavigation` (subscribes to `RelaySession.OnStateChanged` and runs
     `InvokeAsync(Recover)`; it must never throw).
   - Override `OnErrorAsync` to log via `ILogger<RelayErrorBoundary>` with Region and URI. This
     matches house style: `JobCard.razor.cs:17` already injects `ILogger<T>`.
   - Default fallback: one compact line, "‹Region› couldn't be displayed: ‹message›", with a
     **Retry** button. The stack trace stays collapsible and appears in Development only.
     `ExpandedJobView` currently always dumps the stack.
   - Variants: a `Compact` mode for cards and fields.
3. ✓ **Fix the existing `ExpandedJobView` boundary, which never recovers.** Its `@key` sits on the
   inner `DynamicComponent`, and `OverlayJob` is mounted with a constant key
   (`MainLayout.razor:53`). After one failure every job opened later shows the stale error. Move
   the key onto the boundary (or use `ResetKey = (job, iteration)`), and recover on
   `OnIterationChanged`/`OnJobUpdated`. Also: `ExpandedJobView.razor:2` reads
   `Service.CurrentJob.IsIterative` before the null check at `:10`. Snapshot `CurrentJob` once.

## Layer 1: shell regions (MainLayout); each survives the others

| # | Where | Wraps | Reset | Pri |
|---|---|---|---|---|
| S1 | `MainLayout.razor:43` | `@Body` (main screen) | `ResetOnNavigation`, keyed on CurrentMain + P/S/V/F/FD/FI, **not** JobId. Main.razor reuses keyed screen instances, so it must reset on navigation | High |
| S2 | `MainLayout.razor:53` | `<OverlayJob>` | `ResetKey = Session.JobId`; fallback includes "Close job" | High |
| S3 | `MainLayout.razor:59-90` | the overlay `@switch` (Settings, Queues, Personal, Storage) | `ResetKey = Session.CurrentOverlay`; fallback needs its own Close button, because OverlayBase's goes down with the overlay | High |
| S4 | `TopBar.razor:7` | `<Breadcrumbs/>` only, so user menu and theme stay alive | `ResetOnNavigation` | Med-High |
| S5 | `MainLayout.razor:101` / `RightDrawer.razor:9-148` | right drawer body | Reset on selection, session or editor-job change; fallback "Close editor" (`SetJob(null)`), otherwise it re-throws straight after Retry | High |
| S6 | `MainLayout.razor:38` | `<LeftBar>` | Retry; fallback shows a sign-out link | Low |
| S7 | `Routes.razor:3` | `<RouteView>`, catastrophic only (MainLayout's own init) | Reload only (`forceLoad`) | Med |

Why S1 matters: `Session.Project/Space/View` are re-queried on each access, but `CurrentMain` comes
from ids alone. When another user or MCP deletes the current space, the screen renders with
`Session.Space == null` (e.g. `SpaceScreen.razor.cs:73-74,84-85,145-146,181`; `RightDrawer.razor:51,76,117`).

## Layer 2: inside regions

### Job cards and previews (the biggest gap)
- ✓ **C1. Job card body:** `Relay/Screens/Main/View/JobCard.razor:160-182`. Wrap the
  Building / `DynamicComponent Type=Job.CardViewType` / `BasicJobCardContent` branch. Keep the
  header and ports outside so the job can still be opened or deleted. Today one broken card crashes
  the board on **every load**.
  - `WarpCardContent` runs a timer render that reads summary data.
  - `WarpExportCardContent:21-43` indexes card.json arrays.
  - `BasicJobCardContent.razor.cs:73-94` does JS import and file-tail in OnAfterRender without
    try/catch.
  - Reset on `(Job.Id, UpdateDate, Status)`. **High.**
- **C2. Whole diagram node:** `DiagramView.razor:61-105`, around the JobCard/FolderCard/FactoryCard
  switch. Move `@key` onto the boundary. This covers JobCard chrome (relations, ports), which C1
  doesn't.
  - Healthy-state cost is nil: ErrorBoundary emits no DOM, so drag and pan exclusion are unaffected.
  - The fallback tile must keep class `card-common` and `DiagramWidth×Height` so pointerdown
    doesn't start a pan.
  - Same in FactoryBuilder (reuses DiagramView). **High.**
- **C3. Listing items:** `ListingScreen.razor:36-39` around `@ItemTemplate(item)`. One edit covers
  the project, space and view grids and the View list mode. **High.**
- **C4. Hover previews:** once in `JobCardPreviewRenderer.cs:11` (wrap the JobCard it builds).
  Hovering any `JobLine` builds a full JobCard, including in Breadcrumbs, SpaceCard,
  JobProperties, QueueJobCard and ViewCard. Today a breadcrumb hover can kill the circuit. **High.**
- **C5. Home activity strip:** `HomeScreen.razor:12-14`, plus per item at `HomeActivity.razor:75-87`.
  `TakeSnapshot()` walks every project, space and job in OnInitialized. **Medium.**

### View screen
- **V1.** `ViewScreen.razor:18-66`: wrap the list/diagram content region. Leave the toolbar and
  menus outside so the user can switch mode or navigate. Fallback: "Switch to list view". Same at
  `FactoryBuilderScreen.razor:20-26`. **High.**
- **V2.** JobTypeMenu (`ViewScreen.razor:68-83`). RelayMenu always renders its content, and
  `JobTypeMenu.razor:27` dereferences `JobEditor.CurrentJob` after a separate `IsActive` read.
  Prefer the code fix (snapshot); a boundary is optional. **Low.**

### Job editor / properties (right drawer)
- **E1. Per-parameter-field boundary:** `JobParameterDisplay.razor:21,65` and
  `FactoryEditor.razor:106`, with `@key=(Job, property)` moved onto the boundary.
  - Covers the reflection lookups in `UiFieldView.razor.cs:66-68`
    (`DefaultValues[type][name]`, `DataDelegate?.Invoke(Job)` runs job code on every render).
  - Covers the unboxing casts in field views: `UiIntView:19 (int)EditingValue`, `UiInt2..4View`,
    `UiFloat3View`, `UiRangeView`, `UiHealpixView:30 Options[(int)…]`, and
    `UiEnumView:16 EditingValue.ToString()`.
  - Fallback: "Couldn't display this parameter", with **Reset to default** + Recover. **Medium-High.**
- **E2.** `JobEditor.razor:106-116` around `<JobParameterDisplay>` (header and Run/Pause stay
  usable), and `JobEditor.razor:32-47` around `<JobPortDisplay>`. **Medium.**
- **E3.** `JobProperties.razor:219-259`, the Results tab: `:241` calls
  `port.GetResource(it).GetDownloadables()` in render, unguarded, while the same call is try/caught
  in `UpdateResults`. Also the Parameters tab `:203-214`. **Medium.**

### Overlays
- **O1.** `OverlaySettings.razor:5,9,13,17`: one boundary per tab (Users, Queues, State, Logs).
  These are independent admin pages. **Medium.**
- **O2.** `QueueEditor.razor:66-425`: wrap `editor-main` keyed by `_selectedQueue`, so the list
  stays usable to pick or delete a bad queue. **Medium.** It also needs the code fix below.
- **O3.** `OverlayQueues.razor:3-7` per `QueueCard`, and `QueueCard.razor:8` per `QueueJobCard`.
  - `QueueCard.razor.cs:30` reads `GetMostRecentEvent(WaitingStarted).Timestamp` with no null
    check, and older jobs lack that event (`JobActivity.cs:52-56`).
  - One old job currently kills the Queues overlay for admins. **High.**
- **O4.** `OverlayStorage.razor:76` around `<StorageTreemap>`: post-import JS errors are uncaught.
  **Low-Medium.**

### Dialogs (outside all regions)
- ✓ **D1.** `CreateSpaceDialog.razor:43` hosts `<FileBrowser>` directly, without the boundary that
  `FileBrowserDialog` got in the recent fix. **High.**
- **D2.** Bodies of `CreateProjectDialog` and `CreateViewDialog`. **Low.**

### Inside job expanded views (granularity only; the host boundary already prevents circuit death)
- `TomogramViewer` in ExtractParticles and Denoising, keyed by tomogram path:
  `MapHeader.ReadFromFile` runs on partial MRCs in `OnParametersSetAsync`.
- `Class2DSelection` lists in `Class2DSelectExpandedView.razor:18-38`: stale stats arrays cause
  IndexOutOfRange.
- Warp plots: `WarpTrends` (`WarpExpandedView.razor:105`), `WarpAstigmatismPlot` (`:114`),
  `WarpParticleHistogram`. Today a plot JSException blanks the live-session controls.
- `TiltStackViewer` call sites: `TiltAngles.First()/Last()` on an empty array, in render.
- `IterationPagination` and `LogPanel` next to the ExpandedJobView boundary.

## Not boundary problems: code fixes, ranked

These either bypass ErrorBoundary entirely or keep re-throwing after Recover().

**Process- and circuit-fatal:**
1. ✓ `UiEmdbEntryView.razor:44-47`: the `Timer.Elapsed += async (…) => await InvokeAsync(...)`
   handler is `async void` on a thread-pool thread. Any exception, including one raced with
   Dispose, **crashes the server process**, not just the circuit. Wrap it in try/catch, or replace
   it with a CancellationToken debounce.
2. ✓ `RelaySession.cs:601`: `async void HandleLocationChanged`. A throw during back/forward
   navigation kills the circuit. Add try/catch + log + toast.
3. ✓ `QueueJobCard.razor.cs:167,206`: `async void` click handlers that call `Space.Views.First()`.
   Change them to `async Task`.
4. ✓ `GlobalTooltip.razor.cs:108`: `async void Dispose` does JS interop. Change it to
   `IAsyncDisposable` with try/catch.
5. `DiagramView.razor.cs:73-82`: `async void HandleZoomToFit` catches only JSException. It also
   needs to catch `JSDisconnectedException` and `OperationCanceledException` (the JS timeout), and
   the same applies in `DisposeAsync` (`:135`).

**Notification fan-out that aborts or misroutes:**
6. ✓ `EventExtensions.InvokeAllAsync` (`Refund/Utils/EventExtensions.cs:27-46`): the first
   throwing handler aborts the rest, and the exception surfaces in whichever component called
   `NavigateToAsync`. Add a per-handler try/log.
7. ✓ `ExpandedJobViewService.NotifyCurrent` (`:541-559`): same issue. It is fatal via the
   pagination click, which sits outside any boundary. `Class2DExpandedView.razor.cs:93` catches only
   JsonException around `File.ReadAllText`.
8. `JobEditorService.Notify` → `JobEditor.HandleJobChanged`: exceptions surface at the caller of
   `SetJob` (the main view card's boundary), not in the editor.

**Off-dispatcher state mutation (same class as the QueueEditor crash):**
9. ✓ `JobCard.razor.cs:191-232` `HandleSelectionChanged`: rebuilds `_relationParent/_relationChild`
   on the thread pool while render enumerates them.
10. ✓ `ProjectScreen.razor.cs:87-88`: the ProjectDeleted handler calls `Session.NavigateToAsync`
    off-dispatcher, which fans out to every session subscriber on the thread pool.
11. ✓ `UserEditor.razor.cs:49-55`: `_draftValues.Clear()` off-dispatcher.
12. `Breadcrumbs.razor.cs:143-162,176-210`: `LoadData()` off-dispatcher. Its `Dispose` (`:415-421`)
    also doesn't unsubscribe.
13. Most job expanded views mutate fields in `OnJobUpdated`/`OnIterationChanged` before calling
    `InvokeAsync`: the Ts alignment views (×4), Ctf, TemplateMatch, SelectParticles,
    ThresholdStatistics, Class3D/BeamClass3D/InitialReference, Class2DSelect, MotionAndCTF2D, and
    WarpExpandedView. `Class2DExpandedView.HandleJobUpdated` (`:77-81`) is the correct pattern:
    load into locals, then assign inside `InvokeAsync`.
14. `TomogramViewer.razor.cs:444`: `_ = LoadFullVolumeWithCancellation()`.
    - IO errors go unobserved, so the viewer spins forever.
    - Slices are mutated inside `Task.Run`.
    - The token is cancelled only on dispose, so switching tomograms mixes dimensions, which leads
      to IndexOutOfRange in `TomogramSliceViewer:161`.
    - `Dispose` disposes the semaphore that `finally` then releases.

**Systemic data race:**
15. ✓ `ReadOnlySpace.Jobs/Edges/Views` (`ReadOnlySpace.cs:114-129`) enumerate live `List<T>`
    without a lock, so a concurrent create throws "Collection was modified" mid-render (ProjectCard,
    ViewCard, RightDrawer, …). `View.cs:78-80` already uses `SnapshotList`, which is the model to
    follow.

**Smaller:**
16. ✓ `QueueEditor.razor.cs:296`: `new Regex(...{variable}...)` from free-text custom-variable
    names, without `Regex.Escape`. A name like `a(` throws on every render. `IsMatch(null)` also
    throws on a null template.
17. `MainLayout` never runs `Dispose` (no `@implements IDisposable`), so its session subscriptions
    leak. `OverlayQueues.DisposeAsync` is also never called.
18. `MainLayout.razor.cs:88-101`: when the cookie is authenticated but `FindUser` returns null
    (deleted user), the app renders with `Session.User == null`. Redirect to logout instead.
19. ✓ `Program.cs:244`: `UseExceptionHandler("/Error")`, but no `/Error` route exists.
20. `FileService.GetUrl(null)` throws (reached from ServerImage, Thumbnail and TiltStackViewer).
    Make it null-safe rather than adding boundaries around leaf thumbnails.
21. Fragile job-view init, which blanks the whole view even with the host boundary:
    - ImportDataSetTs `.cs:119-121`, DeselectTilts `.cs:118-128`.
    - SelectParticles `razor:74,98` (dictionary indexers).
    - Ts alignment views `.cs:129`.
    - Ctf `.cs:342-403`, ExtractParticles `.cs:113`.

## Suggested order

1. Step 0 (error bar CSS, `RelayErrorBoundary`, fix ExpandedJobView recovery).
2. Code fixes 1–7 (process/circuit-fatal; boundaries can't help).
3. Shell S1–S5, then C1–C4, D1, O3, E1. These cover nearly every user-visible crash path.
4. Off-dispatcher fixes 9–14 and the ReadOnlySpace snapshot (15).
5. Remaining medium/low boundaries and fixes as convenient.

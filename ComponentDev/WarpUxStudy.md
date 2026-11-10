# Warp in Relay: UX study

8 November 2026. Working design hypotheses for review with Warp's owner. These are grounded in source code, product documentation and operator task analysis; they are **not** results from facility-user interviews or measured usability studies.

## Objective

Help a facility operator decide whether collection is producing useful data, whether preprocessing is keeping up, and whether intervention is needed. The expanded view should support a short health check and a focused investigation. Showing every available value is not the objective.

## Evidence and interpretation

### Original Warp

The [local frame-series guide](../Warp/docs/user_guide/warp/quick_start_warp_frame_series.md) describes a useful division between Overview and Fourier & Real Space, collapsing settings to merge the image/spectrum inspection views, zooming dense plots, histograms, clicking points to inspect a movie, keyboard previous/next navigation, and hiding overlays by default. Its gain-reference section says dimensions alone cannot establish correct flip orientation; its CTF setup notes that many parameters only need setting once per microscope.

Code anchors in [MainWindow.xaml](../Warp/Warp/MainWindow.xaml): Start button at line 345; Overview at line 361; astigmatism vector plot at line 476. The [current Warp worktree's guide](../../warp_fixnoise2map/docs/user_guide/warp/quick_start_warp_frame_series.md) has inspection behavior at lines 281–320 and filter semantics at lines 328–348. These line numbers refer to the inspected snapshot, not a stable public API.

Preserve the behavioral ideas: clear selection, linked evidence, accessible filtering, collapsible settings, processed/outdated/filtered/manual-exclusion distinctions, and optional overlays. Do not assume the large Start button, status pie or permanent expert parameter list are essential to Warp's identity. Criticism of those elements is our design judgment, not historical user feedback.

### Domain and product sources

- [Original Warp paper](https://www.nature.com/articles/s41592-019-0580-y): describes global stage drift and local beam-induced motion, spatial defocus variation, and paired real/Fourier-space inspection. This supports exposing the underlying evidence when a scalar looks abnormal; it does not justify automatically diagnosing a microscope fault.
- [CryoSPARC exposure curation](https://guide.cryosparc.com/guides-for-v3/tutorial-manually-curate-exposures): combines image, Thon rings/CTF fit and motion trajectories for assessing exposures, including broken/thick ice, carbon edges and excessive motion.
- [CryoSPARC Live UI](https://guide.cryosparc.com/live/ui-overview): documents exposure following, individual-exposure inspection, overview metrics and separate session navigation. Useful precedent, not a template to copy wholesale.
- [CryoSPARC Live troubleshooting](https://guide.cryosparc.com/live/faqs-and-troubleshooting): distinguishes failed exposures, failed GPU workers and auxiliary-job failures, with different recovery actions. A successful processing stage is not a statement about scientific quality.

## Information priorities

### Default

- Unambiguous session/sample identity and microscope/camera preset.
- Run state, last movie received, last result produced, and processing lag/backlog. Worker activity/target belongs near this operational state.
- Recent collection quality, with the full session available as context. Align CTF fit, motion and one selectable metric vertically on the same time axis.
- Recent accepted fraction as well as total yield. Lifetime totals can conceal deterioration at the end of a long run.
- A selected movie's image and CTF evidence. Following the latest must be explicit, and selecting history must stop automatic selection changes.
- Distinct pending, processing-error, quality-filtered, manually-excluded and outdated states.

### On demand

- Expert processing parameters, pool/job details, logs, individual stage timings and failure tracebacks.
- Motion trajectories, astigmatism vector plot, local defocus and other diagnostic overlays.
- Raw/enhanced side-by-side comparison; ordinary viewing can use a toggle without duplicating the image permanently.
- Full micrograph table, detailed export history, manual denoiser training controls.
- 2D classification when real batch evidence exists. Every batch needs a time/sample context; a class gallery alone does not establish that the current collection is healthy.

### Remove from the default view

- Five completed-stage checkmarks plus five cumulative stage counts and progress bars.
- Permanent training thumbnail walls and explanatory paragraphs about the implementation.
- Equally prominent persistent buttons for every export variant.
- Four narrow, side-by-side plots with unrelated horizontal positions. At 12,480 items and roughly 350 pixels per plot, around 36 movies occupy each horizontal pixel.
- Long rows of 82-pixel thumbnails presented as detailed diagnostic evidence. A short strip of roughly 6–10 images at 120–160 pixels is a starting hypothesis for gross comparisons; fine judgments still need the selected image and zoom. These are design starting points, not validated perceptual thresholds.

## Failure signatures: evidence before interpretation

| Observation | Show together | Possible explanations, not diagnoses |
|---|---|---|
| No new results | Last arrival, last completion, waiting count, active/target workers | Acquisition pause, transfer/path issue, unavailable workers, failed processing |
| Quality worsens over an interval | Time-aligned CTF fit and motion; good/bad image and spectrum | Different ice region, drift/charging, optical changes, fitting failure |
| Strong motion | Motion magnitude and per-frame/local trajectory; image sharpness and CTF | Stage drift or beam-induced motion; one scalar cannot separate them reliably |
| CTF outliers | Fitted defocus, astigmatism, spectrum and fit; requested defocus if available | Focus/optics problem, low signal or fitting failure. Expected defocus cycling is not an alarm |
| Dark/empty regions or uneven particle density | Several images from the selected interval; raw/enhanced comparison | Carbon edge, contamination, broken/thick ice, aggregation, low concentration |
| Picks increase while useful signal decreases | Picks overlaid on enhanced and original image; later 2D evidence | Contaminants/noise being picked. More picks is not automatically better |
| Repeated quality pattern | Time plus acquisition grouping/image-shift position, if metadata supplies it | Position-dependent issue worth investigating; do not invent grouping from filenames |

Label the axis honestly: acquisition time and arrival time are different measurements. If acquisition timestamps are unavailable, say arrival time or acquisition order. Do not visually compress a transfer gap into apparent continuous acquisition.

## Minimal setup and return path

1. Choose a reusable microscope/camera/acquisition preset and input location. Inherit calibrated microscope constants.
2. Verify session-specific raw pixel size, dose with explicit units, gain/reference orientation, EER interpretation, output location and worker pool.
3. Show one discovered movie's filename, dimensions and frame interpretation alongside the preset summary. Relay-side readability and worker-side readability are distinct checks.
4. Inspect the first result and continue automatically. Expert controls remain available under Advanced.
5. On return, show recent history and saved settings. While stopped, a parameter change previews the affected stages before resuming. Retain the user's movie and time selection.

This is a proposed setup flow. A simulated input check in ComponentDev must be labeled as such and must not claim actual remote-worker validation.

## Task-based evaluation rubric

Score each task 0–2: **0** missing, misleading or requires guessing; **1** possible but slow/context-heavy; **2** direct with sufficient evidence. Record time to relevant evidence, context switches, ambiguous statuses and plausible wrong conclusions. The target times below are evaluation goals, not measured results.

| Task | Success criteria |
|---|---|
| Morning triage after 10,000 movies | Within 30 seconds, identify deterioration in the last hour despite good lifetime totals; distinguish a recent problem from historically poor data |
| Silent input for eight minutes | Distinguish absent incoming data from slow processing without opening logs or assuming a microscope fault |
| Investigate a bad interval | Select/brush the interval; compare a representative good/bad image, spectrum and motion; preserve the interval while deciding what to exclude or investigate |
| Start a new user's run | Reuse a microscope preset; verify gain/pixel/dose interpretation; start and inspect the first result without visiting every expert setting |
| Change picking and export | Stop; change blob diameter; understand that motion/CTF/denoising are retained; resume and export available accepted results without losing place |

Reject designs that achieve low click counts by placing everything on screen. Aesthetic review should also ask whether dominant elements correspond to dominant tasks, whether visual weight is spent on useful evidence, and whether uncertainty is expressed without alarmist decoration.

## Generation 1 review

The team separated facility workflow, interaction design and visual design, with the integrating agent implementing and browser-testing the resulting candidates. Monitor-first and image-first workspaces were evaluated against the five tasks above. The original all-in-one prototype remains at `/warp-session-previous` for comparison.

Monitor won as the default: aligned acquisition tracks make a sustained change visible before a user knows which image to inspect. Image-first survives as a separate Inspect workspace. Setup and Exports move off the monitoring surface. We removed cumulative stage inventories, permanent training/2D galleries and redundant action bars from the primary view.

The first facility review provisionally scored 7/10. Concrete objections drove the next iteration: the acceptance denominator described 200 results while plots showed 1,000 movies; stopped sessions could say “Processing” on partial results; changing camera preset left incompatible gain/frame settings; inspection began 267px below the top; and range investigation was hard-coded to the example episode. These were review findings, not measured user performance.

## Generation 2 review

Generation 2 added general interval brushing across four aligned tracks, persistent domain/range state, matching window summaries, honest partial/pending states, coherent acquisition presets and a single inspection toolbar. Fixed-scale, fixed-contrast comparison places CTF/motion/ice readings under each image. The existing Relay power-spectrum viewer remains the scientific evidence control.

The second independent facility review scored the tasks 2, 2, 1, 2, 2 (9/10). This is a heuristic rubric score, not evidence of a measured usability improvement. Browser checks separately exercised 12,480-movie rendering, range selection and preservation, manual exclusion, available-result comparison, preset changes, stage-specific invalidation, two-tab state and real STAR download.

The remaining objections were useful: pending movies could appear as valid comparison fixtures; clearing manual exclusion sounded like overriding quality filters; and “current selection” made export scope ambiguous after adding a brush. The final pass guards comparison evidence, chooses processed reference/sample movies, disables comparison for wholly pending intervals, says “Clear manual exclusion”, and explicitly describes whole-session accepted-result export.

## Final refinement

- Inspect starts its image near 178px rather than 267px. At 1440px width, the main image is 520px and the CTF spectrum remains 344px square; the one-dimensional fit curve uses less height. These sizes are design hypotheses checked in screenshots, not perceptual thresholds established by research.
- The exposure number has space for six digits. Comparison uses matched 480px images; nearby thumbnails remain navigation aids rather than primary evidence.
- An entirely pending recent window explains why CTF data is absent and offers “Show latest results”. It does not silently change the acquisition domain.
- Return-to-latest clears any fixed historical center, so following does not eventually leave plots pinned to old data.
- No automatic microscope-fault diagnosis is implemented. Scenario observations are illustrative design-study content.

Still to validate with real operators and data: acquisition timestamps and time windows, acquisition-position metadata, gain orientation, remote path checking, motion trajectories, astigmatism/local defocus diagnostics, actual warning policies and longitudinal performance. The current synthetic acquisition-order view cannot establish a literal last-hour collection trend. The production manager, workers and Beam integration remain outside this mockup.

## Final browser verification

The final build passed. The nine-step workflow regression and two viewer-lifecycle tests passed. Additional browser checks confirmed that unavailable comparison images stay unavailable, wholly pending intervals cannot launch a comparison, the pending-window recovery action is clickable, and Return to latest restores the trailing domain. No JavaScript page errors were observed.

At 1440 × 900, Monitor, Inspect, Compare, input-stall and backlog views fit without horizontal or vertical document overflow. Setup intentionally scrolls to 975px. Monitor and Inspect also fit at 1920 × 1080; the dark Monitor variant was captured at 1440 × 900. These are layout checks, not performance benchmarks or human usability measurements.

## Owner-directed revision · 9 November 2026

The owner's review corrected the previous iteration's overemphasis on minimalism. The next pass uses the same three roles, with independent source review and rendered follow-up:

- Facility workflow implemented the full Setup surface, calibration preset management, real Relay browser integration and explicit input checks.
- Interaction design implemented immediate plot-header filters, pagination, multi-selection and shared curation semantics.
- Visual design implemented paired real/Fourier evidence, the original axial astigmatism plot and a dedicated Picking workspace.

The user-directed sequence is now Setup → Monitor → Inspect → Picking → Movies → Exports, with the last workspace remembered locally. Repeated mechanisms use one vocabulary: Filter rules, Keep, Exclude; Apply and Revert for processing drafts. Numeric filters are live and affect exports. Processing parameters remain stopped-only.

The circular plot preserves Warp's Δdefocus·cos(2θ), Δdefocus·sin(2θ) convention, positive Y downward: 0° at right, 45° below, 90° left, 135° above. One full turn represents 180° axial orientation. It has selection, zoom and an immediate magnitude filter. This is a display of synthetic metrics; the CTF image fixtures remain illustrative.

We removed the micrograph comparison workspace and the scenario-specific explanatory paragraphs. Training waiting/progress, processing errors and input silence instead have direct operational feedback. Dense forms show their ordinary settings without the old narrow column; wide layouts reveal advanced groups. The large table keeps its metrics close enough to compare and uses additional width for selected-movie evidence.

Two rendered passes exposed real implementation faults: default web-component sizes defeated intended density, an inline canvas aspect ratio inflated the spectrum curve, and a hidden astigmatism canvas could throw on a negative radius. Those were fixed and lifecycle cleanup now uses stable IDs. The large table preview mounts only when visible, rather than allocating hidden image buffers on laptops.

Thirteen browser workflow checks passed: workspace persistence, Revert, actual folder selection, truthful input checks, preset CRUD, bulk Keep/filter semantics, export scope/failure exclusion, pagination/context retention, error navigation, picking-only invalidation, shared run state/locking, first-model training and astigmatism zoom. Viewer lifecycle tests and the new hidden-canvas regression also pass. These are implementation checks and agent reviews, not facility-user usability measurements.

Final rendering was checked at 1440 × 900, 1512 × 982 and 3840 × 2160 with no browser page/console errors. All five main workspaces fit at the MacBook-sized1512 × 982 viewport. At1440 × 900, Setup has23px of vertical scroll; the other workspaces fit. At native4K, Inspect has a1px rounding overflow. Two additional browser regressions confirmed that disabled picking does not leave inactive stages permanently outdated and disabling denoising cancels paused training.

### Scope/proportion correction

The owner clarified that the astigmatism distribution is collection-level evidence. It now appears once, as an alternate collection plot in Monitor, and is absent from Inspect and the table’s selected-movie preview. Per-movie spectra return to about two-thirds of micrograph width (520px micrograph /343px spectrum at1440×900). Laptop and4K renders verified the proportion and single placement with no page errors.

### Inline collection diagnostics and particle selection (9 November)

Picking and extraction processing controls now live in Setup's existing output group and share Apply/Revert. Picking is an image/overlay workspace with a Setup shortcut. The four acquisition tracks have half-height plotting areas and inclusive lower/upper limits, applied immediately to movie selection and exports.

Monitor shows the axial astigmatism distribution beside a joint particle-score histogram below the acquisition tracks, with no collection sub-tab. The histogram matches `WarpLib/Tools/BlobPicker.cs`: Pearson correlation (`rlnAutopickFigureOfMerit`) versus local power (`wrpAutopickPower`, variance in input intensity squared). It includes every pick from nonfailed, up-to-date processing, independently of movie QC exclusion. Numeric bounds or a dragged rectangle select particles immediately; both the picking overlay and particle export use the same bounds. These are result-selection criteria, not processing parameters. The old mock picking threshold has been removed from Setup because the actual blob picker emits all positive maxima for subsequent selection.

The prototype generates deterministic scores for its existing fixture coordinates. The histogram uses the full coordinate population, with shared aggregation over repeated fixture variants; it does not imply live worker ingestion. Production should ingest the two actual STAR columns.

### Persisted histogram state

Persist only the global aggregate histogram and bin geometry, counts, format version, and compact settings/model, cohort, and source signatures alongside the mini JSON. Do not persist per-movie or per-fixture histogram contributions. A valid reboot restores the aggregate directly, without reading particle STAR contents. Runtime contributions may be held temporarily to add newly finished results; they are not durable session state. Recalculating the aggregate when processing settings are applied is acceptable.

The mockup uses schema 2: one 64 × 48 bin array and three compact signatures. It reconstructs its small runtime cohort from already loaded movie metadata, validates the saved aggregate, and reads only a changed fixture when incrementally adding or removing a result after a warm restore. Schema 1 caches with persisted contributions are ignored and rebuilt once. Exact counts for a newly changed particle-selection range, overlays, and exports can read underlying particle scores on demand; the density histogram does not replace those scores.

### Modern Setup parameters and density

Setup now uses the current WarpTools vocabulary: input filename pattern, cumulative dose per movie, and output sampling in Å/px. Output location belongs to Relay's job, not a user parameter. New sessions default to automatic motion and defocus grids; actual dimensions are determined by WarpLib from dose, field of view, frame count and available patches. This is implemented in `Movie.ProcessShift.cs` and `Movie.ProcessCTF.cs`, not just advertised in CLI help. Existing manual grids survive migration. Legacy per-frame dose lacks a frame count, so the prototype explicitly reports its40e⁻/Å² migration default rather than inventing a conversion.

All booleans are Fluent switches. Dropdowns use supported `ListSize.Small` rather than overriding the height of medium-size internals. Numeric input widths are bounded; paired limits and triples of grid dimensions share rows. The three stacked columns on a laptop become six stage groups on a wide display. The Advanced switch starts off regardless of viewport size; it reveals specialist import, optics, fitting, training and export controls within the relevant groups.

### Setup grouping adjustment

Gain reference/correction/orientation move to Collection. EER groups sit beside total dose, with a minimum file-size discovery limit. Pixel size and “Bin to pixel size” form a pair in Microscope; amplitude contrast is a routine microscope parameter. Phase plate is a routine CTF switch. Headerless import is removed. Training begins at the requested sample count, so a separate first-training trigger is removed from the UI and ignored at runtime.

The separate Inspect tab is removed. Monitor contains the interactive micrograph viewer alongside CTF, including Average/Enhanced, previous/next exposure and Filter rules/Keep/Exclude. Movie-table links and double-clicks open the selected movie in Monitor; obsolete Inspect URLs and remembered tabs resolve to Monitor. The smaller spectrum-to-micrograph proportion is retained.

# Warp session prototype

Run from the Relay repository:

```sh
ASPNETCORE_ENVIRONMENT=Development dotnet run --project ComponentDev --no-launch-profile --urls http://127.0.0.1:5237
```

Open http://127.0.0.1:5237/warp-session, or choose **Warp session** in ComponentDev navigation. Development mode is needed for referenced projects' static web assets when running an unpublished build.

The page uses Relay's Fluent UI framework and its actual `ThumbnailPanel`, `SingleAxisScatter`, `AmplitudeSpectrumViewer`, and `MicrographViewer`. The expanded-view layout is isolated in ComponentDev; no job type is registered in production Relay.

## Try it

The current design is at `/warp-session` (also `/warp-study`); `?view=Setup` can open a specific workspace. The first visit opens Setup. Thereafter this preview remembers the last workspace in browser local storage, under the session-specific key. Previous layout: `/warp-session-previous`.

- **Setup:** dense acquisition, microscope/camera, motion, CTF, enhancement/training and output controls. Advanced sections expand on wide displays and remain individually collapsible. Apply/Revert is shared with Picking; edits remain drafts across tabs and lock during processing.
- **Presets:** Load, Save as, Update and Delete operate on persisted calibration presets. Input/output paths, sample-specific dose and picking settings remain collection-local. Gain/defect references and detector interpretation are part of calibration. Saving a preset is distinct from applying a processing draft.
- **Browse and Check input:** folder/file buttons use Relay's actual file browser. Check input reads a matching movie and enabled calibration files and probes the output folder with a unique delete-on-close file. It reports Relay-host accessibility only; image decoding/orientation and remote-worker access are not verified.
- **Monitor:** 12,480 records; shared acquisition-order tracks; immediate acceptance limits in each plot header. Brush an interval and Inspect or apply Filter rules / Keep / Exclude. Image and spectrum appear together, with the spectrum about two-thirds of the micrograph width. Monitor’s collection-plot selector switches between acquisition trends and the axial astigmatism distribution; the latter uses original Warp's doubled-angle coordinates.
- **Inspect:** micrograph and spectrum/fit, with readings above the graphics and nearby thumbnails. The side-by-side micrograph comparison was removed.
- **Picking:** dedicated image/overlay workspace with diameter, threshold, separation, enable and extraction box controls. Existing synthetic coordinates are displayed; changing parameters queues simulated reprocessing, not a real live picker evaluation.
- **Movies:** explicit tab with 25/50/100-row pages, search, sorting, status filters and multi-selection. Select page and Select all matching are distinct. Batch actions share the same Filter rules / Keep / Exclude semantics as plots and images. Large displays add selected-row image/spectral evidence alongside the table. Astigmatism is not repeated in per-movie evidence. Page, search, sorting and selection survive a visit to Inspect.
- **Curation:** Keep overrides numeric filters, but never makes failed or unavailable results exportable. Exclude prevents export; curation does not stop preprocessing. Failed, unprocessed, partial, ready, outdated, filtered, kept and excluded have shared colors across views.
- **Training:** the global status and first-model waiting banner distinguish waiting from active training. Existing models remain usable during fine-tuning. The Denoiser startup scenario begins with 96 movies; Start reaches the training trigger and shows progress.
- **Exports:** actual downloadable STAR tables from all accepted available results across the whole session. A brushed range is not an implicit export scope. Production Relay export jobs are not created by this prototype.

## Scope and state

`WarpSessionPreview` is a singleton. It holds 12,480 distinct compact movie records and shared metric/thumbnail lists. `WarpSessionTrends` caches projections and gates scatter rendering so selection-only changes do not resend all four series. Completion batches rebuild the relevant projections. The existing viewers still own per-component decoded image buffers and the scatter control retains per-view copies; this mockup does **not** implement the proposed production diagnostic cache or prove a production memory bound.

Sixteen deterministic image sets are generated on first use (MRC, Warp XML, PNG and STAR), with low-contrast and directional-drift image variants. Comparison PNGs use fixed contrast. CTF fixtures are illustrative, not estimates fitted to these synthetic condition variants. Images and scientific metrics are synthetic and intended for UI review. Full-size particle extraction, worker dispatch, directory watching, training, and scheduling are simulated. A prototype run never allocates cluster resources.

State is saved to `Path.GetTempPath()/relay-warp-preview/session-mini.json`, alongside fixture and export files. Set `WarpPreview__Directory` to choose another location. The checkpoint contains settings, per-movie stage flags/metrics, selection, pool target, model version, and export history. Reboot reloads the checkpoint into the singleton and leaves processing stopped. There is no multi-file atomic snapshot or artifact retention mechanism. Resetting the sample scenario preserves settings and existing exports.

The production design remains: Relay gathers WarpWorker2 results using the existing disk transport, updates central compact records and mini JSON, and copies available results when an export is requested. Reuse transport and pool integration; do not transplant the desktop queue.

## Checks

```sh
node --test tests/Relay.FluentMigration/viewer-lifecycle.test.mjs tests/Relay.FluentMigration/warp-astig-lifecycle.test.mjs
WARP_PREVIEW_URL=http://127.0.0.1:5237/warp-session node tests/Relay.FluentMigration/warp-study.mjs
WARP_PREVIEW_URL=http://127.0.0.1:5237/warp-session node tests/Relay.FluentMigration/warp-study-stages.mjs
# Previous layout:
WARP_PREVIEW_URL=http://127.0.0.1:5237/warp-session-previous node tests/Relay.FluentMigration/warp-session.mjs
```

The browser check uses `playwright` by default. Set `PLAYWRIGHT_MODULE` and `CHROMIUM_EXECUTABLE` to use an existing installation. It mutates only the prototype's synthetic session. `SCREENSHOT_DIR` optionally saves review images.

Monitor now keeps acquisition tracks, astigmatism, and the cumulative correlation/local-power particle histogram together. Both sides of movie quality ranges are editable in track headers. Particle selection bounds can be typed or drawn, are saved in the mini JSON, and apply to particle overlays/exports without reprocessing. Only up-to-date, nonfailed picked micrographs contribute to the histogram. Picking/extraction processing settings are in Setup; Picking is a results viewer.

Histogram reboot cache: `session-mini.json` persists only one global aggregate histogram (bins, geometry, counts) plus schema and compact settings/model, eligible-cohort, and fixture-source signatures. Per-movie/per-fixture contributions are runtime-only and are not serialized. A valid schema 2 reboot restores Monitor without reading particle STAR contents; newly finished results add their contribution at runtime. Applying changed processing settings may recalculate the aggregate. Older schema 1 contribution caches and mismatched signatures rebuild once. Exact new selection counts, overlays, and exports load particle coordinates on demand.
Setup uses Fluent switches for all booleans and native `ListSize.Small` dropdowns. Inputs are grouped into compact two/three-column rows with 88–96px numeric controls. Advanced is an explicit switch, off by default at every viewport size. Routine collection settings are a filename wildcard, total exposure, and target pixel size; the output directory is the managed Relay job folder. Both motion and CTF support automatic grids in WarpTools/WarpLib. Manual dimensions remain available and are remembered when Auto is enabled.

Advanced controls: chromatic optics and defects; motion frequency/B-factor limits; CTF window/fit quality/thickness/movie-sum options; deconvolution tuning, denoiser iterations and periodic fine-tuning; optional stack export and skipped frames.

Setup grouping: Collection owns gain correction/reference/orientation, total dose, EER groups and minimum file size (decimal MB; zero disables the size limit). Microscope holds pixel size beside “Bin to pixel size”, voltage, spherical aberration and amplitude contrast. CTF exposes a Phase plate switch for phase-shift fitting. Headerless input is removed. The training sample size is also the first-training trigger; there is no independent trigger control.

The separate Inspect tab is removed. Monitor contains the interactive micrograph viewer alongside CTF, including Average/Enhanced, previous/next exposure and Filter rules/Keep/Exclude. Movie-table links and double-clicks open the selected movie in Monitor; obsolete Inspect URLs and remembered tabs resolve to Monitor. The smaller spectrum-to-micrograph proportion is retained.

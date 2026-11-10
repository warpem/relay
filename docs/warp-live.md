# Warp Live in Relay

Create **Frame-series → Motion & CTF → Warp Live**, then open its expanded view. Setup, processing controls, worker queue selection, filters, picking inspection, and exports live there. Processing parameters are editable only after the execution attempt and its workers have stopped. Filters and Keep/Exclude decisions apply immediately.

## Execution and persistence

`DataManager.WarpLiveSessions` owns one central session per project/space/job. Browser circuits keep only their view, selection, and draft settings. The session publishes compact movie records, not retained `Warp.Movie` objects. Full metadata and particle coordinates are loaded for the selected item or export as needed.

Each Start creates a normal Relay local execution attempt. Its `IPooledJob` worker group uses the existing scheduler submission, cancellation, and resizing machinery. The local manager dispatches typed `WorkerCommands` through `Warp.Workers.Queue.TaskQueue` and keeps `Scheduler.Tick()` running during acquisition gaps. The asynchronous manager does not occupy a local processing slot, so export jobs can run even when Relay has one local slot. There is no second cluster-side manager or copied desktop queue.

The canonical job directory survives resumable local preparation. Attempt-specific `tasks/` is recreated only after the previous attempt has stopped. A coalesced `warp-live-mini.json` stores compact results, settings, curation, model information and the single global particle histogram. No per-movie histogram arrays are serialized. Restart restores the session stopped; Start resumes the stages whose fingerprints no longer match.

The processing graph includes motion correction, aligned averages and independent halves, CTF, deconvolution, denoising, blob picking and extraction. Currently needed motion/average/CTF stages are bundled into one disk task with one raw movie load; CTF-on-average reads only the generated average afterward. Each stage retains its own completion fingerprint. Training is its own GPU task once the configured sample is available. Models have distinct file names, so workers can reuse a loaded model across movies and switch after successful training. Enhancement is independent of picking. Particle scores are Pearson correlation and local variance from the shared Warp blob picker.

## Exports

Export micrographs/particles captures the current available selection and creates a normal local Relay export job with a `MicrographSet` or `ParticleSet` output port. It does not depend on the live session finishing. Particle bounds are applied to individual scores during export; micrograph filters and explicit Keep/Exclude decisions also apply.

Exports copy the available metadata and reference image files. They do not promise permanent immutability of those files. Editing or deleting the referenced data can affect downstream jobs.

## Deployment

Build and deploy the changed **Relay**, **WarpWorker2**, **WarpLib**, and **NativeAcceleration** together. An older worker does not have the frame-series enhancement/training commands or native blob picker. The external GPU queue's existing `warp` module must resolve the updated worker and its CUDA/Torch runtime.

Relay and workers must see the input, gain/defect references, and job directory at matching filesystem paths. Relay requires no GPU for the manager. Use an external scheduler GPU queue; the normal Relay pool controls own the worker lifetime.

## Local development without a GPU

Run `python3 scripts/warp-live-dev.py` from the Relay repository. The launcher builds matching Relay and WarpWorker2 binaries, creates an isolated **MOCK** session, and prints its local URL and login. Start processing in that session's expanded view. The normal Relay worker pool launches real `WarpWorker2 --mock` processes through a development scheduler adapter; the session service, disk task queue, result readers, viewers and export jobs are unchanged.

Use `--no-build` for a fast restart after building the affected projects yourself. Use `--state <directory>` to keep a dedicated development collection across restarts and `--port` to select another local port. `--help` lists controls for synthetic acquisition, processing delays and failures. See [the local runner guide](../scripts/warp-live-dev/README.md) for examples.

The mock worker writes labelled synthetic MRC images, thumbnails, CTF/motion metadata, training halves, a mock model marker, particle score STAR tables and extracted stacks. These exercise file contracts and lifecycle behavior; they do not validate CUDA algorithms or denoising quality. Unsupported mock commands fail explicitly.

The CPU integration tests launch the actual worker executable and cover processing, training, histogram restoration, filtered exports, selective reprocessing and failure recovery:

```sh
dotnet test Refund.Tests/Refund.Tests.csproj --filter FullyQualifiedName~MockWorkerEndToEndTests
```

With the local runner active, the optional browser smoke check exercises Start/Stop/resume, resizing, generated imagery and both export buttons:

```sh
node tests/Relay.FluentMigration/warp-live-mock.mjs <development-state-directory>
```

Install the existing `tests/Relay.FluentMigration` Playwright dependencies first. `PLAYWRIGHT_MODULE` and `PLAYWRIGHT_EXECUTABLE_PATH` can select an existing Playwright installation and browser.

## Verification

CPU-side lifecycle, state, transport and export coverage lives in `Refund.Tests` under Warp Live tests. Shared STAR quoting and blob output tests live in `Warp/Tests`. The denoiser smoke test runs only with `WARP_RUN_CUDA_TESTS=1` on a host with the compatible native CUDA/Torch libraries. It checks prepared halves, training, inference and image sampling. Passing managed tests alone does not validate denoising quality or GPU processing performance on an acquisition dataset.

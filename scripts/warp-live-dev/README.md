# Local Warp Live development

From the Relay checkout, run:

```sh
python3 scripts/warp-live-dev.py
```

Requires Python 3.9+, .NET 10 SDK, and Relay's initialized `Warp` submodule. The launcher builds Relay and **its referenced** `Warp/WarpWorker2`, then opens an isolated development server at `http://127.0.0.1:5241`. It prints the session URL and randomly generated local credentials. Log in and click **Start** inside Warp Live. No GPU or cluster account is needed.

This is the actual Relay job, session manager, external worker pool, disk protocol, and WarpWorker2 executable. A development-only custom scheduler launches local processes, and a private PATH wrapper adds `--mock` to WarpWorker2. Workers generate illustrative outputs; **MOCK data are not scientific results**. No fake catalog, preprocessed movie, or denoiser state is inserted into the application.

## Iterate

```sh
# Reuse your previous settings and results after rebuilding affected projects:
python3 scripts/warp-live-dev.py --no-build

# Separate state and port; 24 total inputs with four arriving immediately by default:
python3 scripts/warp-live-dev.py --state /tmp/my-warp-dev --port 5242 --count 80 --interval 3

# From another terminal (include the same --state when using a custom path):
python3 scripts/warp-live-dev.py status
python3 scripts/warp-live-dev.py pause
python3 scripts/warp-live-dev.py resume
# Gracefully stop the host and its workers from another terminal:
python3 scripts/warp-live-dev.py stop
python3 scripts/warp-live-dev.py inject --condition highmotion
python3 scripts/warp-live-dev.py inject --condition badctf
```

Use Start, Stop, worker count, settings, filters, and exports in the normal UI. `pause` stops new input arrivals, not workers. `--count` is the total input target, including files retained from a previous run. `inject` adds one extra input immediately, even when the source is paused or the count is reached. Other conditions are `normal`, `thickice`, `fewparticles`, and `fail`.

Inputs are valid, small float MRC stacks, written to temporary names and renamed into the watched directory. `--size` (default 256) and `--frames` (default 4) set input dimensions. The four-movie training trigger and ten training iterations keep a fresh session quick. Fresh sessions enable thickness fitting so the synthetic ice episode appears in its plot. Existing session settings are preserved on restart. `--mock-delay-ms` (default 250) adjusts mock command latency.

For a deterministic failure/retry exercise, launch with `--mock-fail-command MovieProcessCTF --mock-fail-path fail`, then `inject --condition fail`. Stop the launcher, restart without the failure options, and retry the failed exposure in the UI.

## State and cleanup

Default state is `/tmp/relay-warp-live-dev-<uid>` (on macOS this resolves to `/private/tmp/...`). `dev-session.json` contains URL, route, and credentials for local browser automation. `logs/relay.log` and `logs/scheduler-*.log` contain application and worker logs. `input/` holds synthetic arrivals; `space/` holds normal Relay job files and exports.

`python3 scripts/warp-live-dev.py stop`, Ctrl+C, or SIGTERM stops the development host and its owned worker processes. Relaunching reuses the isolated state; use a new `--state` directory for a fresh session. Remove an old state directory only after stopping its launcher. The launcher refuses a nonempty directory without its development marker and prevents simultaneous ownership of a state directory. Existing Relay servers on other ports are untouched. Use fresh state after moving the checkout, because queue templates retain the launcher path.

The scheduler socket and state directory are private to the current OS user. Scheduler receipts are in-memory references to processes created by this launcher. Receipts from an earlier launch report canceled; persisted PIDs are never adopted or killed. This local scheduler is only a development helper, not a production cluster adapter.

## Checks

```sh
python3 -m unittest discover -s scripts/warp-live-dev -p 'test_*.py'
```

For the real browser regression, follow the command in [Warp Live documentation](../../docs/warp-live.md). The browser test requires this launcher to be running and intentionally starts/stops its MOCK session.

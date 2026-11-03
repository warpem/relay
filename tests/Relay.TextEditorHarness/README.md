Runs the production text editor/controller against disposable files and a local test login.
Bind this harness to loopback only.

```sh
DOTNET_hostBuilder__reloadConfigOnChange=false dotnet run --project tests/Relay.TextEditorHarness --urls http://127.0.0.1:5287
```

In another terminal (requires installed Google Chrome):

```sh
cd tests/Relay.TextEditorHarness
npm ci
npm test
```

Set `EDITOR_TEST_URL` to use a different loopback port. Run once per harness start;
the checks intentionally modify its temporary files. The harness deletes them on shutdown.

For a large-file browser check (100 MiB by default), run:

```sh
EDITOR_TEST_URL=http://127.0.0.1:5287 node large-file.test.mjs
```

Set `EDITOR_TEST_MIB=300` when starting the harness to test 300 MiB instead.
The check throttles upload to 16 MiB/s, verifies visible progress and plain-text
mode, and checks that the saved file has its original size and the edited first
byte. It deliberately makes `model.getValue()` throw to catch accidental whole-file
serialization. The server-side tests separately exercise 100 MiB streams,
encoding/BOM preservation and failure cleanup.

Run `node log-file.test.mjs` against the harness to check job logs containing ANSI
terminal controls and diagnostics for interrupted downloads. Set `EDITOR_TEST_LOG`
to a local log file path when starting the harness to test that file; the harness
copies it into its temporary directory and never changes the original fixture.

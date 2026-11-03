Relay bundles Monaco 0.57.0 as local ESM assets, with syntax highlighting and JSON
language services. File contents never go to a CDN or through the Blazor circuit.
The checked-in output under `Refund/wwwroot/monaco` lets normal .NET builds and
publishes run without Node or network access.

To regenerate after changing the editor bundle:

```sh
cd scripts/monaco
npm ci
npm run build
```

Commit the generated assets and lockfile together. DOMPurify is pinned to 3.4.17
to include the security fixes beyond Monaco's dependency pin. The build retains
Monaco's MIT license, third-party notices, DOMPurify licenses and extracted bundle
license comments. Review these when upgrading dependencies.

The editor accepts valid UTF-8 (with or without BOM), and BOM-marked UTF-16/32,
up to 512 MiB per source file and UTF-8 upload. Metadata is separate from content:
GET `api/text-editor/{token}` returns metadata, GET `{token}/content` streams UTF-8,
POST `{token}/destination` prepares a Save As destination, and PUT `{token}` accepts
raw UTF-8. The server transcodes using bounded buffers and stages each save beside
its destination before atomic replacement. It preserves the encoding/BOM and Unix
mode bits. Save always overwrites, including external edits or deletion; there
are no file hashes or version checks. Save As confirms existing destinations before
preparing or transferring the upload.

Files backing currently connected Relay spaces (`space.relay`) cannot be edited
or selected as Save As destinations. Protection uses the live connected-space
paths across all projects, resolves symlinks, and is checked again before replacing
the destination, including for editor sessions opened before a space was connected.
Disconnected space files remain editable.

Files of 20 MiB or more open in plain-text mode with costly editor features disabled.
The client still holds the full Monaco model in memory. Downloads decode streamed
UTF-8 chunks; saves encode Monaco snapshot chunks into an upload Blob, avoiding
whole-file JSON and Monaco's `getValue()` heap-operation cutoff. The Blob holds
UTF-8 upload bytes; this is not a paged or constant-memory client editor.
Preparation, upload and download progress are displayed. Upload uses XHR progress;
100% transferred changes to “Finishing save…” until the server confirms replacement.

Line endings are sampled from the beginning of the file: CRLF when the sample
contains CRLF, otherwise LF. Monaco normalizes mixed line endings. Save As paths
are relative to the current file's folder or absolute, and must remain inside the
file browser root, including after symbolic-link resolution. Reverse proxies may
need a corresponding request-body limit/timeouts for large uploads. Normal .NET
build/publish includes all assets without running Node.

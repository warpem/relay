// Only control metadata crosses JS interop. Contents stay here and travel over authenticated HTTP.
let monacoPromise;
const instances = new Map();
const api = (token, suffix = '') => new URL(`api/text-editor/${encodeURIComponent(token)}${suffix}`, document.baseURI);
const largeFileBytes = 20 * 1024 * 1024;
const formatBytes = bytes => `${(bytes / 1024 / 1024).toFixed(1)} MiB`;
const nextFrame = () => new Promise(resolve => setTimeout(resolve, 0));

function loadMonaco() {
    if (!monacoPromise) {
        const css = document.createElement('link');
        css.rel = 'stylesheet';
        css.href = new URL('../monaco/editor.css', import.meta.url).href;
        document.head.append(css);
        monacoPromise = import(new URL('../monaco/editor.js', import.meta.url).href)
            .then(module => module.monaco).catch(error => { monacoPromise = null; css.remove(); throw error; });
    }
    return monacoPromise;
}

async function request(token, options = {}, suffix = '') {
    const response = await fetch(api(token, suffix), { credentials: 'same-origin', cache: 'no-store', ...options });
    let result;
    try { result = await response.json(); } catch { result = {}; }
    if (!response.ok) {
        const error = new Error(result.error || (response.status === 401 || response.status === 403
            ? 'Your session has expired or access was denied. Reopen the editor after signing in.'
            : `The file request failed (${response.status}).`));
        error.status = response.status;
        throw error;
    }
    return result;
}

// XHR exposes real upload progress. A Blob uploads without constructing a whole-file JSON string.
function upload(token, blob, antiforgeryToken, signal, progress) {
    return new Promise((resolve, reject) => {
        const xhr = new XMLHttpRequest();
        const abort = () => xhr.abort();
        const finish = () => signal.removeEventListener('abort', abort);
        xhr.open('PUT', api(token));
        xhr.setRequestHeader('Content-Type', 'text/plain; charset=utf-8');
        xhr.setRequestHeader('RequestVerificationToken', antiforgeryToken);
        xhr.upload.onprogress = event => progress(event.loaded, event.lengthComputable ? event.total : blob.size);
        xhr.upload.onload = () => progress(blob.size, blob.size);
        xhr.onload = () => {
            finish();
            let result;
            try { result = JSON.parse(xhr.responseText); } catch { result = {}; }
            if (xhr.status >= 200 && xhr.status < 300) resolve(result);
            else reject(new Error(result.error || `Saving failed (${xhr.status}). Your edits are still open.`));
        };
        xhr.onerror = () => { finish(); reject(new Error('The upload connection failed. Your edits are still open; retry Save.')); };
        xhr.onabort = () => { finish(); reject(new Error('Upload cancelled.')); };
        signal.addEventListener('abort', abort, { once: true });
        if (signal.aborted) { finish(); reject(new Error('Upload cancelled.')); return; }
        xhr.send(blob);
    });
}

async function download(token, file, signal, progress) {
    const incompleteDownload = cause => new Error('The file download was interrupted. Check Relay’s server log for file encoding or content errors, then retry.', { cause });
    const response = await fetch(api(token, '/content'), { signal, credentials: 'same-origin', cache: 'no-store' })
        .catch(error => { throw signal.aborted ? error : incompleteDownload(error); });
    if (!response.ok) {
        const result = await response.json().catch(() => ({}));
        throw new Error(result.error || `Loading failed (${response.status}).`);
    }
    const reader = response.body.getReader();
    const decoder = new TextDecoder('utf-8', { fatal: true });
    const chunks = [];
    let received = 0;
    const total = file.encoding.startsWith('utf8') ? file.size - (file.encoding === 'utf8bom' ? 3 : 0) : null;
    try {
        for (;;) {
            const { done, value } = await reader.read();
            if (done) break;
            received += value.byteLength;
            chunks.push(decoder.decode(value, { stream: true }));
            progress(received, total);
        }
        chunks.push(decoder.decode());
        return chunks.join('');
    } catch (error) {
        throw signal.aborted ? error : incompleteDownload(error);
    } finally { reader.releaseLock(); }
}

// Snapshot chunks avoid Monaco.getValue(), including its 256M-character heap-operation cutoff.
async function prepareUpload(model, progress, signal) {
    const snapshot = model.createSnapshot();
    const total = model.getValueLength();
    const parts = [];
    const encoder = new TextEncoder();
    let chunk, processed = 0, pending = '', sinceYield = 0;
    while ((chunk = snapshot.read()) !== null) {
        if (signal.aborted) throw new Error('Save cancelled.');
        processed += chunk.length;
        chunk = pending + chunk;
        pending = '';
        // A surrogate pair must not be encoded as two replacement characters at a chunk boundary.
        if (chunk.length && /[\uD800-\uDBFF]/.test(chunk[chunk.length - 1])) {
            pending = chunk.slice(-1); chunk = chunk.slice(0, -1);
        }
        parts.push(encoder.encode(chunk));
        sinceYield += chunk.length;
        if (sinceYield >= 1024 * 1024) {
            progress(processed, total);
            await nextFrame();
            sinceYield = 0;
        }
    }
    if (pending) parts.push(encoder.encode(pending));
    return new Blob(parts, { type: 'text/plain; charset=utf-8' });
}

function languageFor(monaco, path) {
    const name = path.split(/[\\/]/).pop();
    return monaco.languages.getLanguages().find(language => language.filenames?.includes(name) ||
        language.extensions?.some(extension => name.toLowerCase().endsWith(extension)))?.id || 'plaintext';
}

export function open(owner, token, dark, receiver) {
    if (instances.has(owner)) { instances.get(owner).dialog.focus(); return; }
    if (!document.querySelector('link[data-relay-text-editor]')) {
        const css = document.createElement('link');
        css.rel = 'stylesheet';
        css.dataset.relayTextEditor = '';
        css.href = new URL('../css/textFileEditor.css', import.meta.url).href;
        document.head.append(css);
    }
    const previousFocus = document.activeElement;
    const dialog = document.createElement('dialog');
    dialog.className = `relay-text-editor ${dark ? 'dark' : 'light'}`;
    dialog.setAttribute('aria-label', 'Text file editor');
    dialog.innerHTML = `
        <header><strong>Text editor</strong><span class="editor-path"></span><span class="editor-dirty"></span>
            <button type="button" data-action="save" disabled>Save</button>
            <button type="button" data-action="save-as" disabled>Save As…</button>
            <button type="button" data-action="reload" disabled>Reload</button>
            <button type="button" data-action="close">Close</button></header>
        <form class="editor-save-as" hidden>
            <label>Save as <input name="destination" required autocomplete="off" spellcheck="false" aria-label="Destination file path"></label>
            <button type="submit">Save file</button><button type="button" data-action="cancel-save-as">Cancel</button>
            <small>Use an absolute path, or a path relative to the current file’s folder.</small>
        </form>
        <div class="editor-status" role="status" aria-live="polite">Loading editor…</div>
        <progress class="editor-progress" aria-label="File transfer progress" max="100" hidden></progress>
        <div class="editor-surface"></div>
        <footer><span class="editor-format"></span><span>Ctrl/Cmd+S to save · Ctrl/Cmd+Shift+S to save as</span></footer>`;
    const element = selector => dialog.querySelector(selector);
    const button = action => element(`[data-action="${action}"]`);
    const status = element('.editor-status');
    const progressBar = element('progress');
    const form = element('form');
    const destinationInput = form.elements.destination;
    const controller = new AbortController();
    const signal = controller.signal;
    let editor, model, monaco, file, savedVersion, busy = true, everSaved = false, disposed = false;
    const dirty = () => !!model && model.getAlternativeVersionId() !== savedVersion;
    const message = (text, error = false) => { status.textContent = text; status.classList.toggle('error', error); };
    const transferProgress = (phase, loaded, total) => {
        progressBar.hidden = false;
        if (total) {
            const percent = Math.min(100, Math.floor(100 * loaded / total));
            progressBar.value = percent;
            message(phase === 'Preparing upload' ? `${phase} ${percent}%` : `${phase} ${percent}% · ${formatBytes(loaded)} / ${formatBytes(total)}`);
        } else {
            progressBar.removeAttribute('value');
            message(`${phase} ${formatBytes(loaded)}`);
        }
    };
    const largeModel = () => file?.size >= largeFileBytes || (model?.getValueLength() || 0) >= largeFileBytes;
    const modelLanguage = () => largeModel() ? 'plaintext' : languageFor(monaco, file.path);
    const update = () => {
        element('.editor-dirty').textContent = dirty() ? 'Unsaved changes' : '';
        for (const action of ['save', 'save-as', 'reload']) button(action).disabled = busy || !model;
        form.querySelector('button[type="submit"]').disabled = busy;
        destinationInput.disabled = busy;
        editor?.updateOptions({ readOnly: busy });
    };
    const beforeUnload = event => { if (dirty() || busy && model) { event.preventDefault(); event.returnValue = ''; } };
    window.addEventListener('beforeunload', beforeUnload, { signal });
    function cleanup(notify) {
        if (disposed) return;
        disposed = true;
        controller.abort();
        editor?.dispose();
        model?.dispose();
        dialog.close();
        dialog.remove();
        instances.delete(owner);
        previousFocus?.focus();
        if (notify) receiver.invokeMethodAsync('OnTextEditorClosed', everSaved).catch(() => {});
    }
    function close() {
        if (busy && model) { message('Wait for the current request to finish.'); return; }
        if (!dirty() || window.confirm('Discard your unsaved changes?')) cleanup(true);
    }
    async function reload(initial = false) {
        if (!initial && dirty() && !window.confirm('Discard your changes and reload the file from disk?')) return;
        busy = true; update(); message('Loading file…');
        try {
            const loaded = await request(token, { signal });
            let text = await download(loaded.token, loaded, signal, (received, total) => transferProgress('Loading', received, total));
            if (disposed) return;
            message('Opening file…');
            progressBar.removeAttribute('value');
            await nextFrame();
            file = loaded; token = loaded.token;
            editor.setModel(null);
            model?.dispose(); model = null;
            const large = file.size >= largeFileBytes || text.length >= largeFileBytes;
            editor.updateOptions({
                largeFileOptimizations: true, folding: !large, wordBasedSuggestions: large ? 'off' : 'currentDocument',
                occurrencesHighlight: large ? 'off' : 'singleFile', selectionHighlight: !large,
                stickyScroll: { enabled: !large }, links: !large, bracketPairColorization: { enabled: !large }
            });
            model = monaco.editor.createModel(text, large ? 'plaintext' : languageFor(monaco, file.path));
            text = null;
            model.setEOL(file.lineEnding === 'CRLF' ? monaco.editor.EndOfLineSequence.CRLF : monaco.editor.EndOfLineSequence.LF);
            editor.setModel(model);
            savedVersion = model.getAlternativeVersionId();
            model.onDidChangeContent(update);
            element('.editor-path').textContent = file.path;
            element('.editor-path').title = file.path;
            element('.editor-format').textContent = `${file.encoding.toUpperCase()} · ${file.lineEnding}${large ? ' · Large file · Plain text' : ''}`;
            form.hidden = true;
            message('Ready'); editor.focus();
        } catch (error) { if (!disposed) message(error.message, true); }
        finally { busy = false; if (!disposed) { progressBar.hidden = true; update(); } }
    }
    function showSaveAs() {
        if (busy || !model) return;
        form.hidden = false;
        destinationInput.value = file.path;
        destinationInput.focus(); destinationInput.select();
    }
    async function save(destination = null) {
        if (busy || !model) return;
        busy = true; update(); message('Saving…');
        const snapshotVersion = model.getAlternativeVersionId();
        try {
            let saveToken = token;
            if (destination) {
                const target = await request(token, {
                    method: 'POST', signal,
                    headers: { 'Content-Type': 'application/json', RequestVerificationToken: file.antiforgeryToken },
                    body: JSON.stringify({ path: destination })
                }, '/destination');
                if (target.exists && !window.confirm(`“${target.path}” already exists. Replace it with this file?`)) {
                    message('Save As cancelled.'); return;
                }
                saveToken = target.token;
            }
            transferProgress('Preparing upload', 0, model.getValueLength());
            await nextFrame();
            const blob = await prepareUpload(model, (loaded, total) => transferProgress('Preparing upload', loaded, total), signal);
            const result = await upload(saveToken, blob, file.antiforgeryToken, signal, (loaded, total) => {
                if (loaded === total) {
                    progressBar.removeAttribute('value'); message('Finishing save…');
                } else transferProgress('Uploading', loaded, total);
            });
            if (disposed) return;
            token = result.token;
            file.path = result.path;
            savedVersion = snapshotVersion; everSaved = true;
            monaco.editor.setModelLanguage(model, modelLanguage());
            element('.editor-path').textContent = file.path;
            element('.editor-path').title = file.path;
            form.hidden = true;
            message(dirty() ? 'Saved. Newer edits are still unsaved.' : 'Saved');
            editor.focus();
        } catch (error) { if (!disposed) message(error.message, true); }
        finally { busy = false; if (!disposed) { progressBar.hidden = true; update(); } }
    }
    button('save').addEventListener('click', () => save(), { signal });
    button('save-as').addEventListener('click', showSaveAs, { signal });
    button('reload').addEventListener('click', () => reload(), { signal });
    button('close').addEventListener('click', close, { signal });
    button('cancel-save-as').addEventListener('click', () => { form.hidden = true; editor?.focus(); }, { signal });
    form.addEventListener('submit', event => { event.preventDefault(); if (form.reportValidity()) save(destinationInput.value); }, { signal });
    dialog.addEventListener('cancel', event => { event.preventDefault(); close(); }, { signal });
    dialog.addEventListener('keydown', event => {
        if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
            event.preventDefault(); event.stopPropagation();
            if (event.shiftKey) showSaveAs(); else save();
        }
    }, { capture: true, signal });
    document.body.append(dialog);
    dialog.showModal();
    instances.set(owner, { dialog, cleanup });
    // Do not return a long-running promise to Blazor's JS interop timeout.
    loadMonaco().then(async loaded => {
        if (disposed) return;
        monaco = loaded;
        editor = monaco.editor.create(element('.editor-surface'), {
            model: null, theme: dark ? 'vs-dark' : 'vs', automaticLayout: true,
            minimap: { enabled: false }, fontSize: 14, scrollBeyondLastLine: false,
            accessibilitySupport: 'auto', ariaLabel: 'File contents', padding: { top: 12 }
        });
        await reload(true);
    }).catch(error => { if (!disposed) { busy = false; message(`Unable to load Monaco: ${error.message}`, true); } });
}

export function dispose(owner) { instances.get(owner)?.cleanup(false); }

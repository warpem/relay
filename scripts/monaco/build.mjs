import { build } from 'esbuild';
import { copyFile, mkdir, rm } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const outdir = fileURLToPath(new URL('../../Refund/wwwroot/monaco/', import.meta.url));
await rm(outdir, { recursive: true, force: true });
await mkdir(outdir, { recursive: true });
await build({
    absWorkingDir: fileURLToPath(new URL('.', import.meta.url)),
    entryPoints: {
        editor: 'editor.js',
        'editor.worker': 'node_modules/monaco-editor/esm/vs/editor/editor.worker.js',
        'json.worker': 'node_modules/monaco-editor/esm/vs/language/json/json.worker.js'
    },
    outdir, bundle: true, splitting: true, format: 'esm', minify: true,
    target: 'es2022', loader: { '.ttf': 'file' }, legalComments: 'linked'
});
for (const name of ['LICENSE', 'ThirdPartyNotices.txt']) {
    await copyFile(new URL(`node_modules/monaco-editor/${name}`, import.meta.url), `${outdir}/${name}`);
}
await copyFile(new URL('node_modules/dompurify/LICENSE', import.meta.url), `${outdir}/DOMPurify-LICENSE`);
await copyFile(new URL('node_modules/dompurify/LICENSE-MPL', import.meta.url), `${outdir}/DOMPurify-LICENSE-MPL`);

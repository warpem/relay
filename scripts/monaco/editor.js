import * as monaco from 'monaco-editor/editor/editor.api.js';
import 'monaco-editor/features/codeEditor/register.js';
import 'monaco-editor/editor/browser/coreCommands.js';
import 'monaco-editor/features/find/register.js';
import 'monaco-editor/features/clipboard/register.js';
import 'monaco-editor/features/contextmenu/register.js';
import 'monaco-editor/features/bracketMatching/register.js';
import 'monaco-editor/features/comment/register.js';
import 'monaco-editor/features/folding/register.js';
import 'monaco-editor/features/tokenization/register.js';
import 'monaco-editor/features/wordOperations/register.js';
import 'monaco-editor/features/multicursor/register.js';
import 'monaco-editor/features/suggest/register.js';
import 'monaco-editor/basic-languages/monaco.contribution.js';
import 'monaco-editor/language/json/monaco.contribution.js';

self.MonacoEnvironment = {
    getWorker(_id, label) {
        return new Worker(new URL(label === 'json' ? './json.worker.js' : './editor.worker.js', import.meta.url), { type: 'module' });
    }
};
export { monaco };

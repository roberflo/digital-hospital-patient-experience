// Lets node:test render the app's real .tsx components: «@/» resolves to src/, TSX is compiled by the installed TypeScript.
import { existsSync, readFileSync } from 'node:fs';
import { registerHooks } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import ts from 'typescript';
const src = new URL('../src/', import.meta.url);
const local = (base) =>
  ['', '.ts', '.tsx', '/index.ts', '/index.tsx']
    .map((x) => base + x)
    .find((p) => existsSync(p) && !p.endsWith('/'));
registerHooks({
  resolve(specifier, context, next) {
    const inSrc = specifier.startsWith('.') && context.parentURL?.startsWith(src.href);
    const base = specifier.startsWith('@/')
      ? fileURLToPath(new URL(specifier.slice(2), src))
      : inSrc
        ? fileURLToPath(new URL(specifier, context.parentURL))
        : null;
    const file = base && local(base);
    return file
      ? { url: pathToFileURL(file).href, format: 'module', shortCircuit: true }
      : next(specifier, context);
  },
  load(url, context, next) {
    if (!/\.tsx?$/.test(url) || url.includes('/node_modules/')) return next(url, context);
    const file = fileURLToPath(url);
    const { outputText } = ts.transpileModule(readFileSync(file, 'utf8'), {
      compilerOptions: {
        jsx: ts.JsxEmit.ReactJSX,
        module: ts.ModuleKind.ESNext,
        target: ts.ScriptTarget.ES2022,
      },
      fileName: file,
    });
    return { format: 'module', source: outputText, shortCircuit: true };
  },
});

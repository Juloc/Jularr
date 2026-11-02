import { transformSync } from 'esbuild';

// The tests compile one TypeScript module at a time and run it in a vm context that has `exports` and `require` only.
const formats = { CommonJS: 'cjs', ESNext: 'esm' };
const targets = { ES2022: 'es2022' };

function transpileModule(source, { compilerOptions = {} } = {}) {
    const format = compilerOptions.module ?? formats.CommonJS;
    const code = transformSync(source, { loader: 'ts', format, target: compilerOptions.target ?? targets.ES2022 }).code;
    // esbuild's CommonJS output assigns module.exports; the context's own `exports` object receives it.
    return { outputText: format === 'cjs' ? 'var module = { exports };\n' + code + '\nObject.assign(exports, module.exports);\n' : code };
}

export default { transpileModule, ModuleKind: formats, ScriptTarget: targets };

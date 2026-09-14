import { defineConfig } from 'tsup';

/**
 * Three outputs, one source:
 *  - `dist/index.js`  + `dist/index.cjs`  for bundlers (ESM + CJS, tree-shakeable, unminified)
 *  - `dist/dle.global.js`                 for a plain `<script>` tag (IIFE, minified, `window.Dle`)
 *
 * Nothing is fetched from a CDN at build or run time (NFR-14); the package has zero runtime
 * dependencies, so the bundle is exactly the code in `src/`.
 */
export default defineConfig([
  {
    entry: { index: 'src/index.ts' },
    format: ['esm', 'cjs'],
    target: 'es2020',
    platform: 'browser',
    dts: true,
    sourcemap: true,
    clean: true,
    treeshake: true,
    minify: false,
  },
  {
    entry: { 'dle.global': 'src/global.ts' },
    format: ['iife'],
    globalName: 'Dle',
    target: 'es2020',
    platform: 'browser',
    dts: true,
    sourcemap: true,
    // terser (dev-only) squeezes the script-tag build noticeably tighter than esbuild does.
    minify: 'terser',
    terserOptions: {
      ecma: 2020,
      compress: { passes: 3, pure_getters: true, unsafe_arrows: true },
      mangle: true,
      format: { comments: false },
    },
    treeshake: true,
    outExtension: () => ({ js: '.js' }),
  },
]);

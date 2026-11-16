import { defineConfig } from 'tsup';
import pkg from './package.json';

/**
 * Three outputs, one source:
 *  - `dist/index.js`  + `dist/index.cjs`  for bundlers (ESM + CJS, tree-shakeable, unminified)
 *  - `dist/dle.global.js`                 for a plain `<script>` tag (IIFE, minified, `window.Dle`)
 *
 * Nothing is fetched from a CDN at build or run time; the package has zero runtime
 * dependencies, so the bundle is exactly the code in `src/`.
 *
 * Every output starts with the MIT notice, because the self-hosted `dle.global.js` is copied onto
 * other people's servers on its own, without the package's LICENSE file next to it. The `/*!`
 * form marks it as a licence comment. The ESM/CJS builds get it from esbuild's banner; the
 * minified build gets it from terser's preamble, which keeps it on the first line (esbuild's banner
 * would end up inside the IIFE once terser has rewritten it), and terser keeps `/*!` comments.
 */
const licenceBanner =
  `/*! ${pkg.name} v${pkg.version} | MIT License | Copyright (c) 2026 MAGØRS, organisation,. */`;

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
    banner: { js: licenceBanner },
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
      // Every comment is dropped except `/*! ... */` licence comments; the notice is the preamble.
      format: { comments: /^!/, preamble: licenceBanner },
    },
    treeshake: true,
    outExtension: () => ({ js: '.js' }),
  },
]);

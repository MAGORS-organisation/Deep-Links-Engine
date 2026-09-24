// Reports the minified and min+gzip / min+brotli sizes of the IIFE bundle and fails the build
// when the gzip size exceeds the budget. The budget is a product requirement, not a nicety: the
// bundle is loaded on every page view of every customer site, ahead of their own content.
//
// The specification's bound is < 100 kB per SDK (docs/zadanie.md §C.5). This budget is deliberately
// an order of magnitude tighter, because the global build carries the smart banner in two languages
// with full keyboard and screen-reader support, and the point of the check is to notice the day a
// dependency or a feature quietly doubles it — not to squeeze the last kilobyte. Raise it only with
// a reason in the commit message.
import { readFileSync, statSync } from 'node:fs';
import { brotliCompressSync, constants, gzipSync } from 'node:zlib';

const BUDGET_GZIP_BYTES = 10 * 1024;
const file = new URL('../dist/dle.global.js', import.meta.url);

let raw;
try {
  raw = readFileSync(file);
} catch {
  console.error('dist/dle.global.js not found - run `npm run build` first');
  process.exit(2);
}

const gzip = gzipSync(raw, { level: constants.Z_BEST_COMPRESSION }).length;
const brotli = brotliCompressSync(raw, {
  params: { [constants.BROTLI_PARAM_QUALITY]: constants.BROTLI_MAX_QUALITY },
}).length;

const kb = (n) => `${(n / 1024).toFixed(2)} kB`;
console.log(`dist/dle.global.js  minified ${kb(statSync(file).size)}  gzip ${kb(gzip)}  brotli ${kb(brotli)}  (budget gzip ${kb(BUDGET_GZIP_BYTES)})`);

if (gzip > BUDGET_GZIP_BYTES) {
  console.error(`size budget exceeded by ${gzip - BUDGET_GZIP_BYTES} bytes (gzip)`);
  process.exit(1);
}

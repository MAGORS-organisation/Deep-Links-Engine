import { DleError, PROBLEM_TYPES, isDeterministic } from './api.js';
import { VERSION, createDle } from './index.js';
import { CHANNELS, detect } from './platform.js';
import { resolveLanguage } from './smart-banner.js';
import type { DleClient, Language, SmartBannerOptions } from './types.js';

/**
 * Entry point of `dist/dle.global.js`, the build for sites without a bundler:
 *
 * ```html
 * <script src="/vendor/dle.global.js"
 *         data-endpoint="https://links.example.sk"
 *         data-sdk-key="dle_…"
 *         data-banner='{"appName":"Example","openUrl":"https://links.example.sk/aB3xK9pQ"}'
 *         data-language="auto"></script>
 * ```
 *
 * The script reads its configuration from its own `<script>` tag and exposes the API as
 * `window.Dle`. `data-banner="true"` shows the banner for the current page (`document.title`
 * and `location.href`); a JSON object supplies full {@link SmartBannerOptions}. Consent is
 * not configurable from markup on purpose: call `Dle.getInstance().setConsent(...)` from your
 * consent tool, so a page cannot claim consent it never asked for.
 *
 * The global surface is deliberately smaller than the ESM one (size budget): bundler users
 * import the full module.
 */
export { CHANNELS, DleError, PROBLEM_TYPES, VERSION, createDle, detect, isDeterministic, resolveLanguage };
export type * from './types.js';

let instance: DleClient | null = null;

/** The client created from the script tag's attributes, or `null` when there were none. */
export function getInstance(): DleClient | null {
  return instance;
}

function warn(message: string, error?: unknown): void {
  (globalThis as { console?: { warn?: (...a: unknown[]) => void } }).console?.warn?.('[dle]', message, error);
}

function parseBanner(raw: string | null, doc: Document): SmartBannerOptions | undefined {
  if (raw === null || raw === '' || raw === 'false') return undefined;
  const defaults: SmartBannerOptions = {
    appName: doc.title,
    openUrl: (globalThis as { location?: { href?: string } }).location?.href ?? '',
  };
  if (raw === 'true') return defaults;
  try {
    const parsed: unknown = JSON.parse(raw);
    if (isRecord(parsed)) return { ...defaults, ...(parsed as Partial<SmartBannerOptions>) };
  } catch {
    /* reported below */
  }
  warn('data-banner must be "true" or a JSON object');
  return undefined;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

/** Create the client from `<script data-endpoint data-sdk-key …>`. Idempotent. */
export function boot(doc: Document | undefined = (globalThis as { document?: Document }).document): DleClient | null {
  if (instance !== null || doc === undefined) return instance;
  const current = doc.currentScript;
  const script = current?.hasAttribute('data-sdk-key') ? current : doc.querySelector('script[data-sdk-key]');
  if (script === null) return null;
  const endpoint = script.getAttribute('data-endpoint');
  const sdkKey = script.getAttribute('data-sdk-key');
  if (endpoint === null || sdkKey === null) return null;
  const language = script.getAttribute('data-language');
  const smartBanner = parseBanner(script.getAttribute('data-banner'), doc);
  try {
    instance = createDle({
      endpoint,
      sdkKey,
      ...(smartBanner === undefined ? {} : { smartBanner }),
      language: language === 'en' || language === 'sk' ? language : ('auto' satisfies Language),
    });
  } catch (error) {
    warn('initialisation failed', error instanceof Error ? error.message : error);
  }
  return instance;
}

boot();

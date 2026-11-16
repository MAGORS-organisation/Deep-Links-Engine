import type { BulkLinkItem, CreateLinkRequest, OgMeta, RoutingRule } from '../api';

export interface ParsedImportBatch {
  items: BulkLinkItem[];
  skipped: { line: number; reason: string }[];
}

interface ParseOptions {
  domainId: string;
  preserveSlugs: boolean;
}

type JsonRecord = Record<string, unknown>;

function asRecord(value: unknown): JsonRecord | null {
  return typeof value === 'object' && value !== null && !Array.isArray(value) ? (value as JsonRecord) : null;
}

function asString(value: unknown): string | undefined {
  if (typeof value !== 'string') {
    return undefined;
  }
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : undefined;
}

function slugFromShortLink(shortLink: string | undefined): string | undefined {
  if (!shortLink) {
    return undefined;
  }
  try {
    const parsed = new URL(shortLink);
    const segment = parsed.pathname.split('/').filter(Boolean).pop();
    return segment && segment.trim().length > 0 ? segment.trim() : undefined;
  } catch {
    return shortLink.replace(/^\/+/, '') || undefined;
  }
}

function parseTags(raw: unknown): string[] {
  if (Array.isArray(raw)) {
    return raw.map((x) => (typeof x === 'string' ? x.trim() : '')).filter(Boolean);
  }
  if (typeof raw === 'string') {
    return raw
      .split(/[;,|]/)
      .map((s) => s.trim())
      .filter(Boolean);
  }
  return [];
}

function buildRules(
  targetUrl: string,
  deeplinkPath: string | undefined,
  iosStoreUrl: string | undefined,
  androidStoreUrl: string | undefined,
): RoutingRule[] {
  const rules: RoutingRule[] = [];

  if (iosStoreUrl) {
    rules.push({
      id: 'ios',
      when: { platform: ['ios'] },
      then: {
        action: 'app_or_store',
        store_url: iosStoreUrl,
        ...(deeplinkPath ? { deeplink_path: deeplinkPath } : {}),
      },
    });
  }

  if (androidStoreUrl) {
    rules.push({
      id: 'android',
      when: { platform: ['android'] },
      then: {
        action: 'app_or_store',
        store_url: androidStoreUrl,
        ...(deeplinkPath ? { deeplink_path: deeplinkPath } : {}),
      },
    });
  }

  rules.push({
    id: 'default',
    when: null,
    then: {
      action: 'web',
      url: targetUrl,
      ...(deeplinkPath ? { deeplink_path: deeplinkPath } : {}),
    },
  });

  return rules;
}

function buildLinkFromObject(raw: JsonRecord, index: number, options: ParseOptions): BulkLinkItem | string {
  const fdlInfo = asRecord(raw.dynamicLinkInfo) ?? raw;
  const androidInfo = asRecord(fdlInfo.androidInfo);
  const iosInfo = asRecord(fdlInfo.iosInfo);
  const socialInfo = asRecord(fdlInfo.socialMetaTagInfo);
  const analyticsInfo = asRecord(fdlInfo.analyticsInfo);
  const playAnalytics = asRecord(analyticsInfo?.googlePlayAnalytics);
  const rawUtm = asRecord(raw.utm);

  const targetUrl =
    asString(fdlInfo.link) ??
    asString(raw.target_url) ??
    asString(raw.targetUrl) ??
    asString(raw.url);

  if (!targetUrl) {
    return 'Missing target URL ("link" or "target_url").';
  }

  const rawSlug =
    asString(raw.slug) ??
    asString(fdlInfo.slug) ??
    slugFromShortLink(asString(raw.shortLink) ?? asString(raw.short_url) ?? asString(raw.shortUrl));

  const slug = options.preserveSlugs ? rawSlug : undefined;
  const deeplinkPath = asString(raw.deeplink_path) ?? asString(raw.deeplinkPath);
  const iosStore = asString(iosInfo?.iosFallbackLink) ?? asString(raw.ios_store_url) ?? asString(raw.iosFallbackLink);
  const androidStore =
    asString(androidInfo?.androidFallbackLink) ?? asString(raw.android_store_url) ?? asString(raw.androidFallbackLink);

  const ogObj = asRecord(raw.og);
  const ogTitle = asString(ogObj?.title) ?? asString(socialInfo?.socialTitle);
  const ogDesc = asString(ogObj?.description) ?? asString(socialInfo?.socialDescription);
  const ogImage = asString(ogObj?.image_url) ?? asString(socialInfo?.socialImageLink);
  const og: OgMeta | undefined =
    ogTitle || ogDesc || ogImage
      ? {
          ...(ogTitle ? { title: ogTitle } : {}),
          ...(ogDesc ? { description: ogDesc } : {}),
          ...(ogImage ? { image_url: ogImage } : {}),
        }
      : undefined;

  const utm: Record<string, string> = {};
  if (rawUtm) {
    for (const [k, v] of Object.entries(rawUtm)) {
      const s = asString(v);
      if (s) utm[k] = s;
    }
  }
  const utmMap: [string, unknown][] = [
    ['utm_source', playAnalytics?.utmSource ?? raw.utm_source],
    ['utm_medium', playAnalytics?.utmMedium ?? raw.utm_medium],
    ['utm_campaign', playAnalytics?.utmCampaign ?? raw.utm_campaign],
    ['utm_term', playAnalytics?.utmTerm ?? raw.utm_term],
    ['utm_content', playAnalytics?.utmContent ?? raw.utm_content],
  ];
  for (const [k, v] of utmMap) {
    const s = asString(v);
    if (s && !utm[k]) utm[k] = s;
  }

  const explicitRules = Array.isArray(raw.routing_rules) ? (raw.routing_rules as RoutingRule[]) : undefined;
  const routingRules =
    explicitRules && explicitRules.length > 0
      ? explicitRules
      : buildRules(targetUrl, deeplinkPath, iosStore, androidStore);

  const tags = parseTags(raw.tags);

  const link: CreateLinkRequest = {
    domain_id: options.domainId,
    ...(slug ? { slug } : {}),
    ...(asString(raw.title) ?? ogTitle ? { title: asString(raw.title) ?? ogTitle } : {}),
    ...(asString(raw.description) ? { description: asString(raw.description) } : {}),
    target_url: targetUrl,
    ...(deeplinkPath ? { deeplink_path: deeplinkPath } : {}),
    routing_rules: routingRules,
    ...(og ? { og } : {}),
    ...(Object.keys(utm).length > 0 ? { utm } : {}),
    ...(tags.length > 0 ? { tags } : {}),
  };

  return {
    ref: rawSlug ?? `row-${index + 1}`,
    link,
  };
}

function parseCsvRows(text: string): string[][] {
  const clean = text.charCodeAt(0) === 0xfeff ? text.slice(1) : text;
  const rows: string[][] = [];
  let row: string[] = [];
  let cell = '';
  let inQuotes = false;

  for (let i = 0; i < clean.length; i++) {
    const ch = clean[i];
    if (inQuotes) {
      if (ch === '"' && clean[i + 1] === '"') {
        cell += '"';
        i++;
      } else if (ch === '"') {
        inQuotes = false;
      } else {
        cell += ch;
      }
      continue;
    }

    if (ch === '"') {
      inQuotes = true;
    } else if (ch === ',') {
      row.push(cell);
      cell = '';
    } else if (ch === '\r' && clean[i + 1] === '\n') {
      row.push(cell);
      rows.push(row);
      row = [];
      cell = '';
      i++;
    } else if (ch === '\n' || ch === '\r') {
      row.push(cell);
      rows.push(row);
      row = [];
      cell = '';
    } else {
      cell += ch;
    }
  }

  if (cell.length > 0 || row.length > 0) {
    row.push(cell);
    rows.push(row);
  }

  return rows.filter((r) => r.some((c) => c.trim().length > 0));
}

function normalizeHeader(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '_');
}

function parseCsvBatch(text: string, options: ParseOptions): ParsedImportBatch {
  const matrix = parseCsvRows(text);
  const headerRow = matrix[0];
  if (!headerRow || matrix.length < 2) {
    return { items: [], skipped: [{ line: 1, reason: 'CSV requires a header row and at least one data row.' }] };
  }

  const headers = headerRow.map(normalizeHeader);
  const items: BulkLinkItem[] = [];
  const skipped: { line: number; reason: string }[] = [];

  for (let r = 1; r < matrix.length; r++) {
    const values = matrix[r];
    if (!values) continue;
    const obj: JsonRecord = {};
    for (let c = 0; c < headers.length; c++) {
      const key = headers[c];
      if (!key) continue;
      const val = values[c]?.trim() ?? '';
      if (!val) continue;

      if (key === 'link' || key === 'url' || key === 'target_url' || key === 'targeturl') {
        obj.target_url = val;
      } else if (key === 'slug') {
        obj.slug = val;
      } else if (key === 'short_link' || key === 'shortlink' || key === 'short_url') {
        obj.shortLink = val;
      } else if (key === 'title') {
        obj.title = val;
      } else if (key === 'description') {
        obj.description = val;
      } else if (key === 'deeplink_path' || key === 'deeplinkpath') {
        obj.deeplink_path = val;
      } else if (key === 'ios_store_url' || key === 'iosfallbacklink' || key === 'ios_fallback_link') {
        obj.ios_store_url = val;
      } else if (key === 'android_store_url' || key === 'androidfallbacklink' || key === 'android_fallback_link') {
        obj.android_store_url = val;
      } else if (key === 'tags') {
        obj.tags = val;
      } else if (key === 'utm_source' || key === 'utm_medium' || key === 'utm_campaign' || key === 'utm_term' || key === 'utm_content') {
        obj[key] = val;
      } else if (key === 'og_title' || key === 'social_title') {
        const og = asRecord(obj.og) ?? {};
        og.title = val;
        obj.og = og;
      } else if (key === 'og_description' || key === 'social_description') {
        const og = asRecord(obj.og) ?? {};
        og.description = val;
        obj.og = og;
      } else if (key === 'og_image' || key === 'og_image_url' || key === 'social_image_link') {
        const og = asRecord(obj.og) ?? {};
        og.image_url = val;
        obj.og = og;
      }
    }

    const built = buildLinkFromObject(obj, r - 1, options);
    if (typeof built === 'string') {
      skipped.push({ line: r + 1, reason: built });
    } else {
      items.push(built);
    }
  }

  return { items, skipped };
}

/**
 * Parses either a Firebase Dynamic Links (FDL) / DLE JSON payload (single object, array,
 * `{ dynamicLinks: [...] }`, or NDJSON) or a CSV export into ready-to-stream `BulkLinkItem` rows.
 */
export function parseFdlOrCsvImport(rawText: string, options: ParseOptions): ParsedImportBatch {
  const trimmed = rawText.trim();
  if (!trimmed) {
    return { items: [], skipped: [] };
  }

  const items: BulkLinkItem[] = [];
  const skipped: { line: number; reason: string }[] = [];

  if (trimmed.startsWith('{') || trimmed.startsWith('[')) {
    let entries: unknown[];
    try {
      const parsed = JSON.parse(trimmed) as unknown;
      if (Array.isArray(parsed)) {
        entries = parsed;
      } else {
        const rec = asRecord(parsed);
        if (rec && Array.isArray(rec.dynamicLinks)) {
          entries = rec.dynamicLinks;
        } else if (rec && Array.isArray(rec.links)) {
          entries = rec.links;
        } else {
          entries = [parsed];
        }
      }
    } catch {
      // Fall back to NDJSON lines.
      const lines = trimmed.split(/\r?\n/);
      entries = [];
      lines.forEach((line, idx) => {
        const l = line.trim();
        if (!l) return;
        try {
          entries.push(JSON.parse(l) as unknown);
        } catch {
          skipped.push({ line: idx + 1, reason: 'Invalid JSON line.' });
        }
      });
    }

    entries.forEach((entry, idx) => {
      const rec = asRecord(entry);
      if (!rec) {
        skipped.push({ line: idx + 1, reason: 'Expected a JSON object.' });
        return;
      }
      const built = buildLinkFromObject(rec, idx, options);
      if (typeof built === 'string') {
        skipped.push({ line: idx + 1, reason: built });
      } else {
        items.push(built);
      }
    });

    return { items, skipped };
  }

  return parseCsvBatch(trimmed, options);
}

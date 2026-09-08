import type { MessageKey } from '../i18n/en';
import type { Params } from '../i18n';
import type { RoutingRule, RuleAction, RuleCondition } from '../api/types';

/**
 * Client-side mirror of src/Dle.Domain/Routing/RoutingRuleValidator.cs. It exists so the editor
 * can refuse to submit a rule set the control plane would refuse anyway, and say why next to the
 * offending field. The server remains the authority; its problem document is still rendered when
 * it disagrees.
 *
 * Paths use the wire spelling (`rules[2].then.url`) so a server-reported error can be matched to
 * the same field.
 */
export const MAX_RULES = 50;

export interface RuleProblem {
  path: string;
  key: MessageKey;
  params?: Params;
}

export function isDefaultRule(rule: RoutingRule): boolean {
  return rule.when === null || rule.when === undefined;
}

function rulePath(index: number, suffix = ''): string {
  return `rules[${index}]${suffix}`;
}

function validateUrl(url: string | null | undefined, path: string, errors: RuleProblem[]): void {
  if (!url || !url.trim()) {
    return;
  }
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    errors.push({ path, key: 'rules.err.urlNotAbsolute' });
    return;
  }
  const scheme = parsed.protocol.replace(/:$/, '');
  if (scheme !== 'http' && scheme !== 'https') {
    errors.push({ path, key: 'rules.err.urlScheme', params: { scheme } });
  }
}

function validateAction(action: RuleAction | undefined, index: number, errors: RuleProblem[]): void {
  if (!action) {
    errors.push({ path: rulePath(index, '.then'), key: 'rules.err.webNeedsUrl' });
    return;
  }

  if (action.action === 'web' && !action.url?.trim()) {
    errors.push({ path: rulePath(index, '.then.url'), key: 'rules.err.webNeedsUrl' });
  }

  if ((action.action === 'app_or_store' || action.action === 'store_only') && !action.store_url?.trim()) {
    errors.push({ path: rulePath(index, '.then.store_url'), key: 'rules.err.storeNeedsUrl' });
  }

  validateUrl(action.url, rulePath(index, '.then.url'), errors);
  validateUrl(action.store_url, rulePath(index, '.then.store_url'), errors);

  const deeplink = action.deeplink_path;
  if (deeplink && (deeplink.includes('://') || deeplink.startsWith('//') || deeplink.startsWith('\\\\'))) {
    errors.push({ path: rulePath(index, '.then.deeplink_path'), key: 'rules.err.deeplinkAbsolute' });
  }
}

function validateCondition(when: RuleCondition, index: number, errors: RuleProblem[]): void {
  const ab = when.ab;
  if (ab && ab.length > 0) {
    const seen = new Set<string>();
    let total = 0;
    ab.forEach((variant, v) => {
      const name = variant.variant?.trim() ?? '';
      if (!name) {
        errors.push({ path: rulePath(index, `.when.ab[${v}].variant`), key: 'rules.err.abVariantRequired' });
      } else if (seen.has(name.toLowerCase())) {
        errors.push({
          path: rulePath(index, `.when.ab[${v}].variant`),
          key: 'rules.err.abVariantDuplicate',
          params: { name },
        });
      } else {
        seen.add(name.toLowerCase());
      }

      if (!Number.isInteger(variant.percent) || variant.percent < 1 || variant.percent > 100) {
        errors.push({ path: rulePath(index, `.when.ab[${v}].percent`), key: 'rules.err.abPercentRange' });
      } else {
        total += variant.percent;
      }
    });
    if (total > 100) {
      errors.push({ path: rulePath(index, '.when.ab'), key: 'rules.err.abSum', params: { total } });
    }
  }

  const window = when.time_window;
  if (window) {
    if (window.from && window.to) {
      const from = Date.parse(window.from);
      const to = Date.parse(window.to);
      if (!Number.isNaN(from) && !Number.isNaN(to) && to <= from) {
        errors.push({ path: rulePath(index, '.when.time_window.to'), key: 'rules.err.windowOrder' });
      }
    }
    window.hours_utc?.forEach((hour, h) => {
      if (!Number.isInteger(hour) || hour < 0 || hour > 23) {
        errors.push({ path: rulePath(index, `.when.time_window.hours_utc[${h}]`), key: 'rules.err.hourRange' });
      }
    });
    window.days_of_week_utc?.forEach((day, d) => {
      if (!Number.isInteger(day) || day < 0 || day > 6) {
        errors.push({ path: rulePath(index, `.when.time_window.days_of_week_utc[${d}]`), key: 'rules.err.dayRange' });
      }
    });
  }

  when.country?.forEach((code, c) => {
    if (!/^[A-Za-z]{2}$/.test(code)) {
      errors.push({ path: rulePath(index, `.when.country[${c}]`), key: 'rules.err.countryCode', params: { code } });
    }
  });
}

/** Returns every problem, never just the first, exactly like the server. */
export function validateRules(rules: readonly RoutingRule[] | null | undefined): RuleProblem[] {
  const errors: RuleProblem[] = [];

  if (!rules || rules.length === 0) {
    errors.push({ path: 'rules', key: 'rules.err.empty' });
    return errors;
  }

  if (rules.length > MAX_RULES) {
    errors.push({ path: 'rules', key: 'rules.err.tooMany', params: { max: MAX_RULES, count: rules.length } });
  }

  const seenIds = new Set<string>();
  let defaults = 0;

  rules.forEach((rule, index) => {
    const id = rule.id?.trim() ?? '';
    if (!id) {
      errors.push({ path: rulePath(index, '.id'), key: 'rules.err.idRequired' });
    } else if (seenIds.has(id)) {
      errors.push({ path: rulePath(index, '.id'), key: 'rules.err.idDuplicate', params: { id } });
    } else {
      seenIds.add(id);
    }

    if (isDefaultRule(rule)) {
      defaults += 1;
      if (index !== rules.length - 1) {
        errors.push({ path: rulePath(index), key: 'rules.err.defaultNotLast' });
      }
    } else {
      validateCondition(rule.when as RuleCondition, index, errors);
    }

    validateAction(rule.then, index, errors);
  });

  if (defaults === 0) {
    errors.push({ path: 'rules', key: 'rules.err.noDefault' });
  } else if (defaults > 1) {
    errors.push({ path: 'rules', key: 'rules.err.multipleDefaults', params: { count: defaults } });
  }

  return errors;
}

export function hasDefaultRule(rules: readonly RoutingRule[]): boolean {
  return rules.some(isDefaultRule);
}

let counter = 0;

export function newRuleId(prefix = 'rule'): string {
  counter += 1;
  return `${prefix}-${Date.now().toString(36)}${counter.toString(36)}`;
}

export function emptyRule(): RoutingRule {
  return {
    id: newRuleId(),
    when: {},
    then: { action: 'web', interstitial: 'auto' },
  };
}

export function defaultRule(targetUrl?: string): RoutingRule {
  return {
    id: 'default',
    when: null,
    then: { action: 'web', url: targetUrl && targetUrl.trim() ? targetUrl : undefined, interstitial: 'auto' },
  };
}

function cleanArray<T>(values: T[] | null | undefined): T[] | undefined {
  if (!values) {
    return undefined;
  }
  const cleaned = values.filter((v) => v !== null && v !== undefined && String(v).trim() !== '');
  return cleaned.length > 0 ? cleaned : undefined;
}

function cleanString(value: string | null | undefined): string | undefined {
  const trimmed = value?.trim();
  return trimmed ? trimmed : undefined;
}

function cleanObject<T extends object>(value: T | null | undefined): T | undefined {
  if (!value) {
    return undefined;
  }
  const entries = Object.entries(value).filter(([, v]) => v !== undefined && v !== null && v !== '');
  return entries.length > 0 ? (Object.fromEntries(entries) as T) : undefined;
}

/**
 * Strips empty members so the document the server receives matches the schema in
 * tests/Dle.ContractTests/Contracts/routing-rules.schema.json (which forbids additional and
 * empty properties). A condition with nothing left becomes... still a condition: the canonical
 * default rule is `when` absent, and the editor decides that explicitly, never by accident.
 */
export function normalizeRules(rules: readonly RoutingRule[]): RoutingRule[] {
  return rules.map((rule) => {
    const then: RuleAction = {
      action: rule.then.action,
      url: cleanString(rule.then.url),
      deeplink_path: cleanString(rule.then.deeplink_path),
      store_url: cleanString(rule.then.store_url),
      referrer_template: cleanString(rule.then.referrer_template),
      interstitial: rule.then.interstitial ?? 'auto',
    };
    const cleanedThen = Object.fromEntries(
      Object.entries(then).filter(([, v]) => v !== undefined),
    ) as RuleAction;

    if (isDefaultRule(rule)) {
      return { id: rule.id.trim(), then: cleanedThen };
    }

    const when = rule.when as RuleCondition;
    const cleanedWhen: RuleCondition = {};
    const platform = cleanArray(when.platform);
    if (platform) cleanedWhen.platform = platform;
    const country = cleanArray(when.country)?.map((c) => c.trim().toUpperCase());
    if (country) cleanedWhen.country = country;
    const region = cleanArray(when.region)?.map((r) => r.trim());
    if (region) cleanedWhen.region = region;
    const language = cleanArray(when.language)?.map((l) => l.trim().toLowerCase());
    if (language) cleanedWhen.language = language;
    const channel = cleanArray(when.channel);
    if (channel) cleanedWhen.channel = channel;
    const osVersion = cleanObject(when.os_version);
    if (osVersion) cleanedWhen.os_version = osVersion;
    const appVersion = cleanObject(when.app_version);
    if (appVersion) cleanedWhen.app_version = appVersion;
    if (when.time_window) {
      const tw = cleanObject({
        from: cleanString(when.time_window.from),
        to: cleanString(when.time_window.to),
        hours_utc: cleanArray(when.time_window.hours_utc),
        days_of_week_utc: cleanArray(when.time_window.days_of_week_utc),
      });
      if (tw) cleanedWhen.time_window = tw;
    }
    const ab = when.ab?.filter((v) => v.variant.trim() || v.percent);
    if (ab && ab.length > 0) {
      cleanedWhen.ab = ab.map((v) => ({ variant: v.variant.trim(), percent: v.percent }));
    }

    return { id: rule.id.trim(), when: cleanedWhen, then: cleanedThen };
  });
}

/** Builds the two platform fallback rules FR-102 asks for, in front of the existing default rule. */
export function platformFallbackRules(iosStoreUrl: string, androidStoreUrl: string, deeplinkPath?: string): RoutingRule[] {
  const rules: RoutingRule[] = [];
  if (iosStoreUrl.trim()) {
    rules.push({
      id: 'ios',
      when: { platform: ['ios'] },
      then: { action: 'app_or_store', store_url: iosStoreUrl.trim(), deeplink_path: deeplinkPath || undefined, interstitial: 'auto' },
    });
  }
  if (androidStoreUrl.trim()) {
    rules.push({
      id: 'android',
      when: { platform: ['android'] },
      then: {
        action: 'app_or_store',
        store_url: androidStoreUrl.trim(),
        deeplink_path: deeplinkPath || undefined,
        referrer_template: 'dl_cid={click_id}&utm_source={utm_source}&utm_campaign={utm_campaign}',
        interstitial: 'auto',
      },
    });
  }
  return rules;
}

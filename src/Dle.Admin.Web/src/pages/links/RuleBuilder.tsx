import { useId, useMemo } from 'react';
import {
  CHANNELS,
  INTERSTITIAL_MODES,
  PLATFORMS,
  ROUTING_ACTIONS,
  type AbVariant,
  type Channel,
  type InterstitialMode,
  type Platform,
  type RoutingActionKind,
  type RoutingRule,
  type RuleAction,
  type RuleCondition,
  type VersionPredicate,
} from '../../api';
import { Field } from '../../components/Field';
import { JsonView } from '../../components/JsonView';
import { fromLocalInput, toLocalInput } from '../../domain/format';
import { defaultRule, emptyRule, isDefaultRule, newRuleId, type RuleProblem } from '../../domain/routingRules';
import { useI18n, type MessageKey } from '../../i18n';
import { IntListInput, ListInput } from './ListInput';
import styles from './RuleBuilder.module.css';

export interface RuleBuilderProps {
  rules: RoutingRule[];
  onChange: (rules: RoutingRule[]) => void;
  /** Client-side problems from validateRules(); shown inline by path. */
  problems: RuleProblem[];
  /** Server problems keyed by wire path (`rules[1].then.url`). */
  serverErrors: Record<string, string[]>;
  /** The link target, offered as the default rule's URL. */
  targetUrl: string;
}

const VERSION_OPS: (keyof VersionPredicate)[] = ['eq', 'gte', 'gt', 'lte', 'lt'];

/**
 * Builds the rule set exactly as src/Dle.Domain/Routing describes it. The default rule (no
 * `when`) is pinned last; without one the parent refuses to save and this component says so at
 * the top of the list (TC-105).
 */
export function RuleBuilder({ rules, onChange, problems, serverErrors, targetUrl }: RuleBuilderProps) {
  const { t } = useI18n();
  const listId = useId();

  const messageFor = useMemo(() => {
    const map = new Map<string, string[]>();
    for (const p of problems) {
      const list = map.get(p.path) ?? [];
      list.push(t(p.key, p.params));
      map.set(p.path, list);
    }
    for (const [path, messages] of Object.entries(serverErrors)) {
      const list = map.get(path) ?? [];
      list.push(...messages);
      map.set(path, list);
    }
    return (path: string): string | undefined => map.get(path)?.join(' ');
  }, [problems, serverErrors, t]);

  const defaultIndex = rules.findIndex(isDefaultRule);
  const hasDefault = defaultIndex >= 0;
  const setLevel = messageFor('rules');

  const update = (index: number, next: RoutingRule) => {
    const copy = rules.slice();
    copy[index] = next;
    onChange(copy);
  };

  const remove = (index: number) => onChange(rules.filter((_, i) => i !== index));

  const move = (index: number, delta: number) => {
    const target = index + delta;
    if (target < 0 || target >= rules.length) return;
    const copy = rules.slice();
    const [item] = copy.splice(index, 1);
    if (!item) return;
    copy.splice(target, 0, item);
    onChange(copy);
  };

  const duplicate = (index: number) => {
    const source = rules[index];
    if (!source) return;
    const copy = rules.slice();
    copy.splice(index + 1, 0, { ...structuredClone(source), id: newRuleId(source.id), when: source.when ?? {} });
    onChange(copy);
  };

  const addRule = () => {
    const copy = rules.slice();
    const insertAt = hasDefault ? defaultIndex : copy.length;
    copy.splice(insertAt, 0, emptyRule());
    onChange(copy);
  };

  const addDefault = () => onChange([...rules, defaultRule(targetUrl)]);

  return (
    <div className={styles.root}>
      <p className="small muted">{t('rules.hint')}</p>

      {!hasDefault && (
        <div role="alert" className="callout callout-bad">
          <strong>{t('rules.missingDefault.title')}</strong>
          <span>{t('rules.missingDefault.detail')}</span>
          <div>
            <button type="button" className="btn btn-sm btn-primary" onClick={addDefault}>
              {t('rules.addDefault')}
            </button>
          </div>
        </div>
      )}
      {hasDefault && setLevel && (
        <div role="alert" className="callout callout-bad">
          <strong>{t('rules.errors.title')}</strong>
          <span>{setLevel}</span>
        </div>
      )}

      <ol className={styles.list} aria-label={t('editor.section.rules')} id={listId}>
        {rules.map((rule, index) => {
          const isDefault = isDefaultRule(rule);
          const path = `rules[${index}]`;
          const ruleError = messageFor(path);
          return (
            <li key={`${index}-${rule.id}`} className={[styles.rule, isDefault ? styles.ruleDefault : ''].filter(Boolean).join(' ')}>
              <div className={styles.ruleHeader}>
                <span className={styles.ruleTitle}>
                  {isDefault ? t('rules.default') : t('rules.rule', { n: index + 1 })}
                  {isDefault && <span className="small muted"> · {t('rules.defaultHint')}</span>}
                </span>
                <span className="row" style={{ gap: 4 }}>
                  {!isDefault && (
                    <>
                      <button type="button" className="btn btn-sm btn-ghost" onClick={() => move(index, -1)} disabled={index === 0} aria-label={`${t('action.moveUp')} ${index + 1}`}>
                        ↑
                      </button>
                      <button
                        type="button"
                        className="btn btn-sm btn-ghost"
                        onClick={() => move(index, 1)}
                        disabled={index >= rules.length - 1 || (hasDefault && index + 1 === defaultIndex)}
                        aria-label={`${t('action.moveDown')} ${index + 1}`}
                      >
                        ↓
                      </button>
                      <button type="button" className="btn btn-sm btn-ghost" onClick={() => duplicate(index)}>
                        {t('action.duplicate')}
                      </button>
                    </>
                  )}
                  <button type="button" className="btn btn-sm btn-ghost" onClick={() => remove(index)}>
                    {t('action.remove')}
                  </button>
                </span>
              </div>

              {ruleError && (
                <p className="error small" role="alert">
                  {ruleError}
                </p>
              )}

              <div className={styles.ruleBody}>
                <Field label={t('rules.id')} hint={t('rules.idHint')} error={messageFor(`${path}.id`)} className={styles.idField}>
                  {(p) => <input {...p} className="input mono" value={rule.id} onChange={(e) => update(index, { ...rule, id: e.target.value })} />}
                </Field>

                {!isDefault && (
                  <ConditionEditor
                    when={rule.when ?? {}}
                    onChange={(when) => update(index, { ...rule, when })}
                    path={`${path}.when`}
                    messageFor={messageFor}
                  />
                )}

                <ActionEditor then={rule.then} onChange={(then) => update(index, { ...rule, then })} path={`${path}.then`} messageFor={messageFor} />
              </div>
            </li>
          );
        })}
      </ol>

      <div className="row">
        <button type="button" className="btn" onClick={addRule}>
          {t('rules.add')}
        </button>
        {!hasDefault && (
          <button type="button" className="btn btn-primary" onClick={addDefault}>
            {t('rules.addDefault')}
          </button>
        )}
      </div>

      <JsonView value={rules} />
    </div>
  );
}

function ConditionEditor({ when, onChange, path, messageFor }: { when: RuleCondition; onChange: (when: RuleCondition) => void; path: string; messageFor: (path: string) => string | undefined }) {
  const { t } = useI18n();
  const set = <K extends keyof RuleCondition>(key: K, value: RuleCondition[K]) => onChange({ ...when, [key]: value });

  const toggle = <T extends string | number>(list: T[] | null | undefined, item: T): T[] => {
    const current = list ?? [];
    return current.includes(item) ? current.filter((x) => x !== item) : [...current, item];
  };

  const ab = when.ab ?? [];
  const abError = messageFor(`${path}.ab`);
  const window = when.time_window ?? {};

  return (
    <fieldset className={styles.section}>
      <legend>{t('rules.when')}</legend>
      <div className={styles.condGrid}>
        <fieldset className={styles.inner}>
          <legend>{t('rules.cond.platform')}</legend>
          <div className="checkbox-grid">
            {PLATFORMS.map((platform) => (
              <label key={platform} className="checkbox">
                <input type="checkbox" checked={(when.platform ?? []).includes(platform)} onChange={() => set('platform', toggle<Platform>(when.platform, platform))} />
                {t(`platform.${platform}`)}
              </label>
            ))}
          </div>
        </fieldset>

        <fieldset className={styles.inner}>
          <legend>{t('rules.cond.channel')}</legend>
          <div className="checkbox-grid">
            {CHANNELS.map((channel) => (
              <label key={channel} className="checkbox">
                <input type="checkbox" checked={(when.channel ?? []).includes(channel)} onChange={() => set('channel', toggle<Channel>(when.channel, channel))} />
                {t(`channel.${channel}`)}
              </label>
            ))}
          </div>
        </fieldset>

        <Field label={t('rules.cond.country')} hint={t('rules.cond.countryHint')} error={messageFor(`${path}.country`) ?? firstIndexed(messageFor, `${path}.country`)}>
          {(p) => <ListInput {...p} mono value={when.country ?? []} onChange={(v) => set('country', v.length ? v : undefined)} transform={(s) => s.toUpperCase()} placeholder="SK, CZ" />}
        </Field>
        <Field label={t('rules.cond.language')} hint={t('rules.cond.languageHint')}>
          {(p) => <ListInput {...p} mono value={when.language ?? []} onChange={(v) => set('language', v.length ? v : undefined)} transform={(s) => s.toLowerCase()} placeholder="sk, cs" />}
        </Field>
        <Field label={t('rules.cond.region')} hint={t('rules.cond.regionHint')} optionalText={t('label.optional')}>
          {(p) => <ListInput {...p} mono value={when.region ?? []} onChange={(v) => set('region', v.length ? v : undefined)} />}
        </Field>

        <VersionEditor label={t('rules.cond.osVersion')} value={when.os_version ?? {}} onChange={(v) => set('os_version', v)} />
        <VersionEditor label={t('rules.cond.appVersion')} value={when.app_version ?? {}} onChange={(v) => set('app_version', v)} />

        <fieldset className={[styles.inner, styles.span2].join(' ')}>
          <legend>{t('rules.cond.timeWindow')}</legend>
          <div className="form-grid">
            <Field label={t('rules.cond.from')}>
              {(p) => <input {...p} className="input" type="datetime-local" value={toLocalInput(window.from)} onChange={(e) => set('time_window', { ...window, from: fromLocalInput(e.target.value) })} />}
            </Field>
            <Field label={t('rules.cond.to')} error={messageFor(`${path}.time_window.to`)}>
              {(p) => <input {...p} className="input" type="datetime-local" value={toLocalInput(window.to)} onChange={(e) => set('time_window', { ...window, to: fromLocalInput(e.target.value) })} />}
            </Field>
            <Field label={t('rules.cond.hours')} hint={t('rules.cond.hoursHint')} error={firstIndexed(messageFor, `${path}.time_window.hours_utc`)}>
              {(p) => <IntListInput {...p} mono value={window.hours_utc ?? []} onChange={(v) => set('time_window', { ...window, hours_utc: v.length ? v : undefined })} placeholder="9, 10, 11" />}
            </Field>
            <div className="field">
              <span className="label">{t('rules.cond.days')}</span>
              <div className="checkbox-grid">
                {[1, 2, 3, 4, 5, 6, 0].map((day) => (
                  <label key={day} className="checkbox">
                    <input
                      type="checkbox"
                      checked={(window.days_of_week_utc ?? []).includes(day)}
                      onChange={() => {
                        const next = toggle<number>(window.days_of_week_utc, day).sort((a, b) => a - b);
                        set('time_window', { ...window, days_of_week_utc: next.length ? next : undefined });
                      }}
                    />
                    {t(`rules.day.${day}` as MessageKey)}
                  </label>
                ))}
              </div>
              {firstIndexed(messageFor, `${path}.time_window.days_of_week_utc`) && (
                <span className="error" role="alert">
                  {firstIndexed(messageFor, `${path}.time_window.days_of_week_utc`)}
                </span>
              )}
            </div>
          </div>
        </fieldset>

        <fieldset className={[styles.inner, styles.span2].join(' ')}>
          <legend>{t('rules.cond.ab')}</legend>
          <p className="small muted">{t('rules.cond.abHint')}</p>
          {abError && (
            <p className="error small" role="alert">
              {abError}
            </p>
          )}
          <div className="stack" style={{ gap: 6, marginTop: 6 }}>
            {ab.map((variant, v) => (
              <div key={v} className={styles.abRow}>
                <Field label={t('rules.cond.abVariant')} error={messageFor(`${path}.ab[${v}].variant`)}>
                  {(p) => (
                    <input
                      {...p}
                      className="input mono"
                      value={variant.variant}
                      onChange={(e) => set('ab', replaceAt(ab, v, { ...variant, variant: e.target.value }))}
                    />
                  )}
                </Field>
                <Field label={t('rules.cond.abPercent')} error={messageFor(`${path}.ab[${v}].percent`)}>
                  {(p) => (
                    <input
                      {...p}
                      className="input"
                      type="number"
                      min={1}
                      max={100}
                      value={variant.percent}
                      onChange={(e) => set('ab', replaceAt(ab, v, { ...variant, percent: Number.parseInt(e.target.value, 10) || 0 }))}
                    />
                  )}
                </Field>
                <button type="button" className="btn btn-sm btn-ghost" onClick={() => set('ab', ab.filter((_, i) => i !== v).length ? ab.filter((_, i) => i !== v) : undefined)}>
                  {t('action.remove')}
                </button>
              </div>
            ))}
            <div>
              <button type="button" className="btn btn-sm" onClick={() => set('ab', [...ab, { variant: ab.length === 0 ? 'a' : ab.length === 1 ? 'b' : `v${ab.length + 1}`, percent: 50 }])}>
                {t('rules.cond.abAdd')}
              </button>
            </div>
          </div>
        </fieldset>
      </div>
    </fieldset>
  );
}

function VersionEditor({ label, value, onChange }: { label: string; value: VersionPredicate; onChange: (next: VersionPredicate | undefined) => void }) {
  const { t } = useI18n();
  return (
    <fieldset className={styles.inner}>
      <legend>{label}</legend>
      <div className={styles.versionGrid}>
        {VERSION_OPS.map((op) => (
          <Field key={op} label={t(`rules.cond.version.${op}`)}>
            {(p) => (
              <input
                {...p}
                className="input mono"
                placeholder="18.0"
                value={value[op] ?? ''}
                onChange={(e) => {
                  const next = { ...value, [op]: e.target.value || undefined };
                  const any = VERSION_OPS.some((k) => next[k]);
                  onChange(any ? next : undefined);
                }}
              />
            )}
          </Field>
        ))}
      </div>
    </fieldset>
  );
}

function ActionEditor({ then, onChange, path, messageFor }: { then: RuleAction; onChange: (then: RuleAction) => void; path: string; messageFor: (path: string) => string | undefined }) {
  const { t } = useI18n();
  const needsStore = then.action === 'app_or_store' || then.action === 'store_only';
  const isWeb = then.action === 'web';
  const isBlock = then.action === 'block';

  return (
    <fieldset className={styles.section}>
      <legend>{t('rules.then')}</legend>
      <div className="form-grid">
        <Field label={t('rules.action')} error={messageFor(`${path}.action`)}>
          {(p) => (
            <select {...p} className="select" value={then.action} onChange={(e) => onChange({ ...then, action: e.target.value as RoutingActionKind })}>
              {ROUTING_ACTIONS.map((action) => (
                <option key={action} value={action}>
                  {t(`rules.action.${action}`)}
                </option>
              ))}
            </select>
          )}
        </Field>
        <Field label={t('rules.action.interstitial')} error={messageFor(`${path}.interstitial`)}>
          {(p) => (
            <select {...p} className="select" value={then.interstitial ?? 'auto'} onChange={(e) => onChange({ ...then, interstitial: e.target.value as InterstitialMode })}>
              {INTERSTITIAL_MODES.map((mode) => (
                <option key={mode} value={mode}>
                  {t(`rules.interstitial.${mode}`)}
                </option>
              ))}
            </select>
          )}
        </Field>
        {!isBlock && (
          <Field label={t('rules.action.url')} hint={t('rules.action.urlHint')} error={messageFor(`${path}.url`)} className="span-2" optionalText={isWeb ? undefined : t('label.optional')}>
            {(p) => <input {...p} className="input mono" type="url" value={then.url ?? ''} onChange={(e) => onChange({ ...then, url: e.target.value })} placeholder="https://" />}
          </Field>
        )}
        {(needsStore || then.action === 'app_only') && (
          <Field label={t('rules.action.storeUrl')} hint={t('rules.action.storeUrlHint')} error={messageFor(`${path}.store_url`)} className="span-2" optionalText={needsStore ? undefined : t('label.optional')}>
            {(p) => <input {...p} className="input mono" type="url" value={then.store_url ?? ''} onChange={(e) => onChange({ ...then, store_url: e.target.value })} placeholder="https://apps.apple.com/…" />}
          </Field>
        )}
        {!isBlock && !isWeb && (
          <Field label={t('rules.action.deeplinkPath')} error={messageFor(`${path}.deeplink_path`)} optionalText={t('label.optional')}>
            {(p) => <input {...p} className="input mono" value={then.deeplink_path ?? ''} onChange={(e) => onChange({ ...then, deeplink_path: e.target.value })} placeholder="/product/123" />}
          </Field>
        )}
        {needsStore && (
          <Field label={t('rules.action.referrerTemplate')} hint={t('rules.action.referrerTemplateHint')} error={messageFor(`${path}.referrer_template`)} optionalText={t('label.optional')}>
            {(p) => (
              <input {...p} className="input mono" value={then.referrer_template ?? ''} onChange={(e) => onChange({ ...then, referrer_template: e.target.value })} placeholder="dl_cid={click_id}&utm_source={utm_source}" />
            )}
          </Field>
        )}
      </div>
    </fieldset>
  );
}

function replaceAt(list: AbVariant[], index: number, item: AbVariant): AbVariant[] {
  return list.map((v, i) => (i === index ? item : v));
}

/** Finds the first message under an indexed path such as `…hours_utc[3]`. */
function firstIndexed(messageFor: (path: string) => string | undefined, prefix: string): string | undefined {
  for (let i = 0; i < 64; i += 1) {
    const message = messageFor(`${prefix}[${i}]`);
    if (message) {
      return message;
    }
  }
  return undefined;
}

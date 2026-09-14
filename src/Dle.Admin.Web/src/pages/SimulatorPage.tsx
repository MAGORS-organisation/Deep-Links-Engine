import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import { CHANNELS, links, type LinkResponse, type SimulateRequest, type SimulateResponse } from '../api';
import { ErrorNotice } from '../components/ErrorNotice';
import { Field } from '../components/Field';
import { JsonView } from '../components/JsonView';
import { PageHeader } from '../components/PageHeader';
import { StatusPill, type PillTone } from '../components/StatusPill';
import { fromLocalInput } from '../domain/format';
import { useAsync } from '../hooks/useAsync';
import { useDebouncedValue } from '../hooks/useDebouncedValue';
import { useI18n, type MessageKey } from '../i18n';

interface ClientForm {
  platform: string;
  os_version: string;
  app_version: string;
  country: string;
  region: string;
  language: string;
  channel: string;
  at: string;
  click_id: string;
  user_agent: string;
}

const EMPTY: ClientForm = { platform: '', os_version: '', app_version: '', country: '', region: '', language: '', channel: '', at: '', click_id: '', user_agent: '' };

const PRESETS: { key: MessageKey; form: Partial<ClientForm> }[] = [
  { key: 'simulator.preset.igSk', form: { platform: 'ios', os_version: '18.0', country: 'SK', language: 'sk', channel: 'in_app_ig' } },
  { key: 'simulator.preset.androidCz', form: { platform: 'android', os_version: '15', country: 'CZ', language: 'cs', channel: 'browser' } },
  { key: 'simulator.preset.desktop', form: { platform: 'desktop', country: 'DE', language: 'de', channel: 'browser' } },
];

const DECISION_TONE: Record<string, PillTone> = {
  web: 'accent',
  store_ios: 'accent',
  store_android: 'accent',
  app_open: 'ok',
  interstitial: 'ok',
  preview: 'neutral',
  blocked: 'bad',
  not_found: 'bad',
  gone: 'bad',
};

const DECISION_KEYS: Record<string, MessageKey> = {
  web: 'decision.web',
  store_ios: 'decision.store_ios',
  store_android: 'decision.store_android',
  app_open: 'decision.app_open',
  interstitial: 'decision.interstitial',
  preview: 'decision.preview',
  blocked: 'decision.blocked',
  not_found: 'decision.not_found',
  gone: 'decision.gone',
};

export default function SimulatorPage() {
  const { id } = useParams<{ id: string }>();
  const { t, locale } = useI18n();
  const navigate = useNavigate();

  const [search, setSearch] = useState('');
  const debounced = useDebouncedValue(search.trim());
  const candidates = useAsync((signal) => links.list({ search: debounced || undefined, limit: 50 }, signal), [debounced]);
  const selected = useAsync((signal) => links.get(id as string, signal), [id], Boolean(id));

  const [form, setForm] = useState<ClientForm>(EMPTY);
  const [result, setResult] = useState<SimulateResponse | null>(null);
  const [error, setError] = useState<unknown>(undefined);
  const [running, setRunning] = useState(false);

  useEffect(() => {
    setResult(null);
    setError(undefined);
  }, [id]);

  const run = async () => {
    if (!id) return;
    setRunning(true);
    setError(undefined);
    try {
      const request: SimulateRequest = {
        platform: form.platform || null,
        os_version: form.os_version.trim() || null,
        app_version: form.app_version.trim() || null,
        country: form.country.trim().toUpperCase() || null,
        region: form.region.trim() || null,
        language: form.language.trim().toLowerCase() || null,
        channel: form.channel || null,
        at: fromLocalInput(form.at),
        click_id: form.click_id.trim() || null,
        user_agent: form.user_agent.trim() || null,
      };
      setResult(await links.simulate(id, request, locale === 'sk' ? 'sk, en;q=0.8' : 'en'));
    } catch (err) {
      setError(err);
      setResult(null);
    } finally {
      setRunning(false);
    }
  };

  const link: LinkResponse | undefined = selected.data;

  return (
    <div className="page">
      <PageHeader title={t('simulator.title')} subtitle={t('simulator.subtitle')} />

      <div className="grid-2" style={{ alignItems: 'start' }}>
        <div className="stack">
          <section className="card">
            <h2 className="card-title">{t('simulator.link')}</h2>
            <div className="stack">
              <Field label={t('simulator.searchLink')}>
                {(p) => <input {...p} className="input" type="search" value={search} onChange={(e) => setSearch(e.target.value)} placeholder={t('links.searchPlaceholder')} />}
              </Field>
              <Field label={t('simulator.pickLink')}>
                {(p) => (
                  <select {...p} className="select mono" value={id ?? ''} onChange={(e) => void navigate(e.target.value ? `/simulator/${e.target.value}` : '/simulator')}>
                    <option value="">—</option>
                    {(candidates.data?.items ?? []).map((l) => (
                      <option key={l.id} value={l.id}>
                        {l.host}/{l.slug}
                        {l.title ? ` — ${l.title}` : ''}
                      </option>
                    ))}
                    {id && !candidates.data?.items.some((l) => l.id === id) && link && (
                      <option value={link.id}>
                        {link.host}/{link.slug}
                      </option>
                    )}
                  </select>
                )}
              </Field>
              {candidates.error ? <ErrorNotice error={candidates.error} onRetry={candidates.reload} compact /> : null}
              {selected.error ? <ErrorNotice error={selected.error} onRetry={selected.reload} compact /> : null}
              {link && (
                <dl className="dl">
                  <dt>{t('links.col.link')}</dt>
                  <dd>
                    <Link to={`/links/${link.id}`} className="mono">
                      {link.short_url}
                    </Link>
                  </dd>
                  <dt>{t('links.col.target')}</dt>
                  <dd className="mono truncate" title={link.target_url}>
                    {link.target_url}
                  </dd>
                  <dt>{t('links.col.rules')}</dt>
                  <dd>{link.routing_rules.map((r) => r.id).join(' → ') || t('label.none')}</dd>
                </dl>
              )}
            </div>
          </section>

          <form
            className="card"
            onSubmit={(e) => {
              e.preventDefault();
              void run();
            }}
          >
            <h2 className="card-title">{t('simulator.presets')}</h2>
            <div className="row" style={{ marginBottom: 'var(--sp-3)' }}>
              {PRESETS.map((preset) => (
                <button key={preset.key} type="button" className="btn btn-sm" onClick={() => setForm({ ...EMPTY, ...preset.form })}>
                  {t(preset.key)}
                </button>
              ))}
              <button type="button" className="btn btn-sm btn-ghost" onClick={() => setForm(EMPTY)}>
                {t('action.clear')}
              </button>
            </div>
            <div className="form-grid">
              <Field label={t('simulator.field.platform')}>
                {(p) => (
                  <select {...p} className="select" value={form.platform} onChange={(e) => setForm({ ...form, platform: e.target.value })}>
                    <option value="">{t('simulator.any')}</option>
                    {(['ios', 'android', 'desktop', 'other'] as const).map((pl) => (
                      <option key={pl} value={pl}>
                        {t(`platform.${pl}`)}
                      </option>
                    ))}
                  </select>
                )}
              </Field>
              <Field label={t('simulator.field.osVersion')}>{(p) => <input {...p} className="input mono" value={form.os_version} onChange={(e) => setForm({ ...form, os_version: e.target.value })} placeholder="18.1" />}</Field>
              <Field label={t('simulator.field.country')}>
                {(p) => <input {...p} className="input mono" value={form.country} maxLength={2} onChange={(e) => setForm({ ...form, country: e.target.value.toUpperCase() })} placeholder="SK" />}
              </Field>
              <Field label={t('simulator.field.language')}>{(p) => <input {...p} className="input mono" value={form.language} onChange={(e) => setForm({ ...form, language: e.target.value })} placeholder="sk" />}</Field>
              <Field label={t('simulator.field.channel')}>
                {(p) => (
                  <select {...p} className="select" value={form.channel} onChange={(e) => setForm({ ...form, channel: e.target.value })}>
                    <option value="">{t('simulator.any')}</option>
                    {CHANNELS.map((channel) => (
                      <option key={channel} value={channel}>
                        {t(`channel.${channel}`)}
                      </option>
                    ))}
                  </select>
                )}
              </Field>
              <Field label={t('simulator.field.appVersion')}>{(p) => <input {...p} className="input mono" value={form.app_version} onChange={(e) => setForm({ ...form, app_version: e.target.value })} placeholder="3.2.0" />}</Field>
              <Field label={t('simulator.field.region')} optionalText={t('label.optional')}>
                {(p) => <input {...p} className="input mono" value={form.region} onChange={(e) => setForm({ ...form, region: e.target.value })} />}
              </Field>
              <Field label={t('simulator.field.at')} optionalText={t('label.optional')}>
                {(p) => <input {...p} className="input" type="datetime-local" value={form.at} onChange={(e) => setForm({ ...form, at: e.target.value })} />}
              </Field>
              <Field label={t('simulator.field.clickId')} optionalText={t('label.optional')}>
                {(p) => <input {...p} className="input mono" value={form.click_id} onChange={(e) => setForm({ ...form, click_id: e.target.value })} placeholder="simulation" />}
              </Field>
              <Field label={t('simulator.field.userAgent')} className="span-2" optionalText={t('label.optional')}>
                {(p) => <textarea {...p} className="textarea mono" rows={2} value={form.user_agent} onChange={(e) => setForm({ ...form, user_agent: e.target.value })} />}
              </Field>
            </div>
            <div className="row row-end" style={{ marginTop: 'var(--sp-3)' }}>
              <button type="submit" className="btn btn-primary" disabled={!id || running}>
                {running ? t('simulator.running') : t('simulator.run')}
              </button>
            </div>
          </form>
        </div>

        <div className="stack">
          <section className="card" aria-live="polite">
            <h2 className="card-title">{t('simulator.result')}</h2>
            {error !== undefined && <ErrorNotice error={error} onRetry={() => void run()} />}
            {!result && error === undefined && <p className="muted small">{t('simulator.empty')}</p>}
            {result && (
              <div className="stack">
                <dl className="dl">
                  <dt>{t('simulator.decision')}</dt>
                  <dd>
                    <StatusPill tone={DECISION_TONE[result.decision] ?? 'neutral'}>{DECISION_KEYS[result.decision] ? t(DECISION_KEYS[result.decision] as MessageKey) : result.decision}</StatusPill>
                  </dd>
                  <dt>{t('simulator.matchedRule')}</dt>
                  <dd>
                    <code>{result.matched_rule_id}</code>
                  </dd>
                  {result.url && (
                    <>
                      <dt>{t('simulator.url')}</dt>
                      <dd className="mono" style={{ wordBreak: 'break-all' }}>
                        {result.url}
                      </dd>
                    </>
                  )}
                  {result.deeplink_path && (
                    <>
                      <dt>{t('simulator.deeplinkPath')}</dt>
                      <dd className="mono">{result.deeplink_path}</dd>
                    </>
                  )}
                  {result.store_url && (
                    <>
                      <dt>{t('simulator.storeUrl')}</dt>
                      <dd className="mono" style={{ wordBreak: 'break-all' }}>
                        {result.store_url}
                      </dd>
                    </>
                  )}
                  {result.ab_bucket !== null && result.ab_bucket !== undefined && (
                    <>
                      <dt>{t('simulator.abBucket')}</dt>
                      <dd className="mono">
                        {result.ab_bucket}
                        {result.ab_variant ? ` → ${result.ab_variant}` : ''}
                      </dd>
                    </>
                  )}
                  <dt>{t('simulator.consentMode')}</dt>
                  <dd className="mono">{result.consent_mode}</dd>
                </dl>
                <h3>{t('simulator.trace')}</h3>
                <ol className="trace">
                  {result.trace.map((line, i) => (
                    <li key={i}>{line}</li>
                  ))}
                </ol>
                <JsonView value={result} />
              </div>
            )}
          </section>

          {link && (
            <section className="card">
              <h2 className="card-title">{t('editor.section.rules')}</h2>
              <ol className="trace">
                {link.routing_rules.map((rule) => (
                  <li key={rule.id} style={{ fontWeight: result?.matched_rule_id === rule.id ? 650 : 400 }}>
                    <code>{rule.id}</code> · {t(`rules.action.${rule.then.action}`)}
                    {!rule.when || Object.keys(rule.when).length === 0 ? <span className="tag" style={{ marginLeft: 6 }}>{t('rules.default')}</span> : null}
                    {result?.matched_rule_id === rule.id && <span className="tag" style={{ marginLeft: 6, background: 'var(--ok-soft)', color: 'var(--ok-soft-fg)' }}>✓</span>}
                  </li>
                ))}
              </ol>
              <div style={{ marginTop: 'var(--sp-3)' }}>
                <JsonView value={link.routing_rules} />
              </div>
            </section>
          )}
        </div>
      </div>
    </div>
  );
}

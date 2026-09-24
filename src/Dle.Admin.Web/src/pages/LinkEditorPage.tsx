import { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import { ApiError, domains, links, type CreateLinkRequest, type LinkResponse, type OgMeta, type RoutingRule, type UpdateLinkRequest } from '../api';
import { ErrorNotice } from '../components/ErrorNotice';
import { Field } from '../components/Field';
import { JsonView } from '../components/JsonView';
import { PageHeader } from '../components/PageHeader';
import { useToast } from '../components/ToastContext';
import { appendUtm, fromLocalInput, parseList, toLocalInput } from '../domain/format';
import { defaultRule, hasDefaultRule, normalizeRules, platformFallbackRules, validateRules } from '../domain/routingRules';
import { useAsync } from '../hooks/useAsync';
import { useI18n } from '../i18n';
import { RuleBuilder } from './links/RuleBuilder';
import { OgPreview } from './links/OgPreview';

interface LinkForm {
  domain_id: string;
  slug: string;
  title: string;
  description: string;
  target_url: string;
  deeplink_path: string;
  routing_rules: RoutingRule[];
  og: OgMeta;
  utm: { key: string; value: string }[];
  tags: string;
  starts_at: string;
  expires_at: string;
  expired_url: string;
  is_active: boolean;
  change_note: string;
}

function emptyForm(domainId = ''): LinkForm {
  return {
    domain_id: domainId,
    slug: '',
    title: '',
    description: '',
    target_url: '',
    deeplink_path: '',
    routing_rules: [defaultRule()],
    og: { type: 'website', twitter_card: 'summary_large_image' },
    utm: [],
    tags: '',
    starts_at: '',
    expires_at: '',
    expired_url: '',
    is_active: true,
    change_note: '',
  };
}

function formFrom(link: LinkResponse): LinkForm {
  return {
    domain_id: link.domain_id,
    slug: link.slug,
    title: link.title ?? '',
    description: link.description ?? '',
    target_url: link.target_url,
    deeplink_path: link.deeplink_path ?? '',
    routing_rules: link.routing_rules.map((r) => ({ ...r, when: r.when ?? null })),
    og: { type: 'website', twitter_card: 'summary_large_image', ...(link.og ?? {}) },
    utm: Object.entries(link.utm ?? {}).map(([key, value]) => ({ key, value })),
    tags: link.tags.join(', '),
    starts_at: toLocalInput(link.starts_at),
    expires_at: toLocalInput(link.expires_at),
    expired_url: link.expired_url ?? '',
    is_active: link.is_active,
    change_note: '',
  };
}

function utmRecord(rows: { key: string; value: string }[]): Record<string, string> {
  const record: Record<string, string> = {};
  for (const row of rows) {
    if (row.key.trim()) {
      record[row.key.trim()] = row.value.trim();
    }
  }
  return record;
}

function cleanOg(og: OgMeta): OgMeta | null {
  const entries = Object.entries(og).filter(([, v]) => typeof v === 'string' && v.trim() !== '');
  const meaningful = entries.filter(([k]) => k !== 'type' && k !== 'twitter_card');
  return meaningful.length === 0 ? null : (Object.fromEntries(entries) as OgMeta);
}

export default function LinkEditorPage() {
  const { id } = useParams<{ id: string }>();
  const isNew = !id;
  const { t, formatDate } = useI18n();
  const toast = useToast();
  const navigate = useNavigate();

  const domainList = useAsync((signal) => domains.list(signal), []);
  const existing = useAsync((signal) => links.get(id as string, signal), [id], !isNew);
  const versions = useAsync(() => links.versions(id as string, 20), [id], !isNew);

  const [form, setForm] = useState<LinkForm>(() => emptyForm());
  const [loadedId, setLoadedId] = useState<string | null>(null);
  const [submitted, setSubmitted] = useState(false);
  const [saving, setSaving] = useState(false);
  const [serverError, setServerError] = useState<ApiError | null>(null);
  const [fallbacks, setFallbacks] = useState({ ios: '', android: '' });

  useEffect(() => {
    if (existing.data && loadedId !== existing.data.id) {
      setForm(formFrom(existing.data));
      setLoadedId(existing.data.id);
    }
  }, [existing.data, loadedId]);

  useEffect(() => {
    if (isNew && !form.domain_id && domainList.data) {
      const preferred = domainList.data.items.find((d) => d.is_default) ?? domainList.data.items[0];
      if (preferred) {
        setForm((f) => ({ ...f, domain_id: preferred.id }));
      }
    }
  }, [isNew, form.domain_id, domainList.data]);

  const problems = useMemo(() => validateRules(form.routing_rules), [form.routing_rules]);
  const missingDefault = !hasDefaultRule(form.routing_rules);
  const serverFieldErrors = serverError?.errors ?? {};
  const fieldError = (name: string) => serverFieldErrors[name]?.[0];

  const patch = <K extends keyof LinkForm>(key: K, value: LinkForm[K]) => setForm((f) => ({ ...f, [key]: value }));

  const applyFallbacks = () => {
    const generated = platformFallbackRules(fallbacks.ios, fallbacks.android, form.deeplink_path || undefined);
    if (generated.length === 0) return;
    const rest = form.routing_rules.filter((r) => !generated.some((g) => g.id === r.id));
    const defaultIndex = rest.findIndex((r) => r.when === null || r.when === undefined);
    const next = rest.slice();
    next.splice(defaultIndex >= 0 ? defaultIndex : next.length, 0, ...generated);
    patch('routing_rules', next);
    toast.push(t('editor.fallbacks.applied'));
  };

  const save = async () => {
    setSubmitted(true);
    setServerError(null);
    if (problems.length > 0 || !form.target_url.trim() || (isNew && !form.domain_id)) {
      toast.error(t('editor.fixErrors'));
      return;
    }
    setSaving(true);
    try {
      const rules = normalizeRules(form.routing_rules);
      const og = cleanOg(form.og);
      let saved: LinkResponse;
      if (isNew) {
        const body: CreateLinkRequest = {
          domain_id: form.domain_id,
          slug: form.slug.trim() || null,
          title: form.title.trim() || null,
          description: form.description.trim() || null,
          target_url: form.target_url.trim(),
          deeplink_path: form.deeplink_path.trim() || null,
          routing_rules: rules,
          og,
          utm: utmRecord(form.utm),
          tags: parseList(form.tags),
          starts_at: fromLocalInput(form.starts_at),
          expires_at: fromLocalInput(form.expires_at),
          expired_url: form.expired_url.trim() || null,
          is_active: form.is_active,
        };
        saved = await links.create(body);
        toast.success(t('links.created'));
        void navigate(`/links/${saved.id}`, { replace: true });
      } else {
        const body: UpdateLinkRequest = {
          title: form.title.trim() || null,
          description: form.description.trim() || null,
          target_url: form.target_url.trim(),
          deeplink_path: form.deeplink_path.trim() || null,
          routing_rules: rules,
          og,
          utm: utmRecord(form.utm),
          tags: parseList(form.tags),
          starts_at: fromLocalInput(form.starts_at),
          expires_at: fromLocalInput(form.expires_at),
          expired_url: form.expired_url.trim() || null,
          is_active: form.is_active,
          change_note: form.change_note.trim() || null,
        };
        saved = await links.update(id as string, body);
        toast.success(t('links.saved'));
        existing.setData(saved);
        setForm(formFrom(saved));
        versions.reload();
      }
    } catch (error) {
      if (error instanceof ApiError) {
        setServerError(error);
      } else {
        toast.error(t('state.unexpected'));
      }
    } finally {
      setSaving(false);
    }
  };

  if (!isNew && existing.error) {
    return (
      <div className="page">
        <PageHeader title={t('editor.titleEdit')} />
        <ErrorNotice error={existing.error} onRetry={existing.reload} />
      </div>
    );
  }

  const previewTarget = appendUtm(form.target_url || 'https://example.com/', utmRecord(form.utm));
  const link = existing.data;

  return (
    <div className="page">
      <PageHeader
        title={isNew ? t('editor.titleNew') : t('editor.titleEdit')}
        subtitle={
          link ? (
            <span className="row">
              <code>{link.short_url}</code>
              <span className="tag">{t('editor.version.n', { n: link.version })}</span>
            </span>
          ) : undefined
        }
        actions={
          <>
            <Link className="btn" to="/links">
              {t('action.back')}
            </Link>
            {link ? (
              <Link className="btn" to={`/simulator/${link.id}`}>
                {t('action.simulate')}
              </Link>
            ) : (
              <span className="small muted">{t('editor.saveFirst')}</span>
            )}
            <button type="button" className="btn btn-primary" onClick={() => void save()} disabled={saving || (submitted && (problems.length > 0 || missingDefault))}>
              {saving ? t('action.saving') : t('action.save')}
            </button>
          </>
        }
      />

      {serverError && (
        <div>
          <p className="small muted" style={{ marginBottom: 6 }}>
            {t('editor.serverErrors')}
          </p>
          <ErrorNotice error={serverError} />
        </div>
      )}

      <form
        className="page"
        onSubmit={(e) => {
          e.preventDefault();
          void save();
        }}
        noValidate
      >
        <section className="card">
          <h2 className="card-title">{t('editor.section.basics')}</h2>
          <div className="form-grid">
            <Field label={t('editor.field.domain')} hint={t('editor.field.domainHint')} error={fieldError('domain_id')}>
              {(p) => (
                <select {...p} className="select" value={form.domain_id} onChange={(e) => patch('domain_id', e.target.value)} disabled={!isNew} required>
                  <option value="">—</option>
                  {(domainList.data?.items ?? []).map((d) => (
                    <option key={d.id} value={d.id}>
                      {d.host}
                      {d.is_default ? ' ★' : ''}
                      {!d.is_active ? ' (inactive)' : ''}
                    </option>
                  ))}
                </select>
              )}
            </Field>
            <Field label={t('editor.field.slug')} hint={t('editor.field.slugHint')} error={fieldError('slug')} optionalText={isNew ? t('label.optional') : undefined}>
              {(p) => <input {...p} className="input mono" value={form.slug} onChange={(e) => patch('slug', e.target.value)} disabled={!isNew} autoComplete="off" spellCheck={false} />}
            </Field>
            <Field label={t('editor.field.title')} error={fieldError('title')} optionalText={t('label.optional')}>
              {(p) => <input {...p} className="input" value={form.title} onChange={(e) => patch('title', e.target.value)} />}
            </Field>
            <Field label={t('editor.field.description')} error={fieldError('description')} optionalText={t('label.optional')}>
              {(p) => <input {...p} className="input" value={form.description} onChange={(e) => patch('description', e.target.value)} />}
            </Field>
            <label className="checkbox">
              <input type="checkbox" checked={form.is_active} onChange={(e) => patch('is_active', e.target.checked)} />
              {t('editor.field.active')}
            </label>
          </div>
        </section>

        <section className="card">
          <h2 className="card-title">{t('editor.section.target')}</h2>
          <div className="form-grid">
            <Field label={t('editor.field.targetUrl')} hint={t('editor.field.targetUrlHint')} error={fieldError('target_url') ?? (submitted && !form.target_url.trim() ? t('rules.err.webNeedsUrl') : undefined)} className="span-2">
              {(p) => <input {...p} className="input mono" type="url" required value={form.target_url} onChange={(e) => patch('target_url', e.target.value)} placeholder="https://" />}
            </Field>
            <Field label={t('editor.field.deeplinkPath')} hint={t('editor.field.deeplinkPathHint')} error={fieldError('deeplink_path')} optionalText={t('label.optional')}>
              {(p) => <input {...p} className="input mono" value={form.deeplink_path} onChange={(e) => patch('deeplink_path', e.target.value)} placeholder="/product/123" />}
            </Field>
          </div>
        </section>

        <section className="card">
          <h2 className="card-title">{t('editor.section.fallbacks')}</h2>
          <p className="small muted" style={{ marginBottom: 'var(--sp-3)' }}>
            {t('editor.fallbacks.hint')}
          </p>
          <div className="form-grid">
            <Field label={t('editor.fallbacks.ios')}>
              {(p) => <input {...p} className="input mono" type="url" value={fallbacks.ios} onChange={(e) => setFallbacks({ ...fallbacks, ios: e.target.value })} placeholder="https://apps.apple.com/app/id…" />}
            </Field>
            <Field label={t('editor.fallbacks.android')}>
              {(p) => <input {...p} className="input mono" type="url" value={fallbacks.android} onChange={(e) => setFallbacks({ ...fallbacks, android: e.target.value })} placeholder="https://play.google.com/store/apps/details?id=…" />}
            </Field>
            <div className="field" style={{ alignSelf: 'end' }}>
              <button type="button" className="btn" onClick={applyFallbacks} disabled={!fallbacks.ios.trim() && !fallbacks.android.trim()}>
                {t('editor.fallbacks.apply')}
              </button>
            </div>
          </div>
        </section>

        <section className="card">
          <h2 className="card-title">{t('editor.section.rules')}</h2>
          <RuleBuilder rules={form.routing_rules} onChange={(rules) => patch('routing_rules', rules)} problems={submitted ? problems : problems.filter((p) => p.path === 'rules')} serverErrors={serverFieldErrors} targetUrl={form.target_url} />
        </section>

        <section className="card">
          <h2 className="card-title">{t('editor.section.og')}</h2>
          <div className="grid-2">
            <div className="stack">
              <Field label={t('editor.og.title')}>{(p) => <input {...p} className="input" value={form.og.title ?? ''} onChange={(e) => patch('og', { ...form.og, title: e.target.value })} />}</Field>
              <Field label={t('editor.og.description')}>
                {(p) => <textarea {...p} className="textarea" rows={2} value={form.og.description ?? ''} onChange={(e) => patch('og', { ...form.og, description: e.target.value })} />}
              </Field>
              <Field label={t('editor.og.image')}>
                {(p) => <input {...p} className="input mono" type="url" value={form.og.image_url ?? ''} onChange={(e) => patch('og', { ...form.og, image_url: e.target.value })} />}
              </Field>
              <div className="form-grid">
                <Field label={t('editor.og.siteName')}>{(p) => <input {...p} className="input" value={form.og.site_name ?? ''} onChange={(e) => patch('og', { ...form.og, site_name: e.target.value })} />}</Field>
                <Field label={t('editor.og.type')}>{(p) => <input {...p} className="input mono" value={form.og.type ?? ''} onChange={(e) => patch('og', { ...form.og, type: e.target.value })} />}</Field>
                <Field label={t('editor.og.twitterCard')}>
                  {(p) => (
                    <select {...p} className="select" value={form.og.twitter_card ?? 'summary_large_image'} onChange={(e) => patch('og', { ...form.og, twitter_card: e.target.value })}>
                      <option value="summary_large_image">summary_large_image</option>
                      <option value="summary">summary</option>
                    </select>
                  )}
                </Field>
              </div>
            </div>
            <div className="stack">
              <span className="label small" style={{ fontWeight: 560 }}>
                {t('editor.og.preview')}
              </span>
              <OgPreview og={form.og} url={form.target_url} />
              <span className="small faint">{t('editor.og.previewHint')}</span>
            </div>
          </div>
        </section>

        <section className="card">
          <h2 className="card-title">{t('editor.section.utm')}</h2>
          <div className="stack">
            {form.utm.map((row, i) => (
              <div key={i} className="row" style={{ alignItems: 'flex-end' }}>
                <Field label={t('editor.utm.key')} className="grow">
                  {(p) => (
                    <input
                      {...p}
                      className="input mono"
                      value={row.key}
                      list="utm-keys"
                      onChange={(e) => patch('utm', form.utm.map((r, j) => (j === i ? { ...r, key: e.target.value } : r)))}
                    />
                  )}
                </Field>
                <Field label={t('editor.utm.value')} className="grow">
                  {(p) => <input {...p} className="input mono" value={row.value} onChange={(e) => patch('utm', form.utm.map((r, j) => (j === i ? { ...r, value: e.target.value } : r)))} />}
                </Field>
                <button type="button" className="btn btn-ghost" onClick={() => patch('utm', form.utm.filter((_, j) => j !== i))}>
                  {t('action.remove')}
                </button>
              </div>
            ))}
            <datalist id="utm-keys">
              {['utm_source', 'utm_medium', 'utm_campaign', 'utm_term', 'utm_content'].map((k) => (
                <option key={k} value={k} />
              ))}
            </datalist>
            <div>
              <button type="button" className="btn btn-sm" onClick={() => patch('utm', [...form.utm, { key: form.utm.length === 0 ? 'utm_source' : '', value: '' }])}>
                {t('editor.utm.add')}
              </button>
            </div>
            {form.utm.length > 0 && (
              <p className="small muted">
                {t('editor.utm.preview')}: <code>{previewTarget}</code>
              </p>
            )}
          </div>
        </section>

        <section className="card">
          <h2 className="card-title">{t('editor.section.validity')}</h2>
          <p className="small muted" style={{ marginBottom: 'var(--sp-3)' }}>
            {t('editor.validity.hint')}
          </p>
          <div className="form-grid">
            <Field label={t('editor.field.startsAt')} error={fieldError('starts_at')} optionalText={t('label.optional')}>
              {(p) => <input {...p} className="input" type="datetime-local" value={form.starts_at} onChange={(e) => patch('starts_at', e.target.value)} />}
            </Field>
            <Field label={t('editor.field.expiresAt')} error={fieldError('expires_at')} optionalText={t('label.optional')}>
              {(p) => <input {...p} className="input" type="datetime-local" value={form.expires_at} onChange={(e) => patch('expires_at', e.target.value)} />}
            </Field>
            <Field label={t('editor.field.expiredUrl')} hint={t('editor.field.expiredUrlHint')} error={fieldError('expired_url')} optionalText={t('label.optional')}>
              {(p) => <input {...p} className="input mono" type="url" value={form.expired_url} onChange={(e) => patch('expired_url', e.target.value)} placeholder="https://" />}
            </Field>
          </div>
        </section>

        <section className="card">
          <h2 className="card-title">{t('editor.section.tags')}</h2>
          <div className="form-grid">
            <Field label={t('editor.field.tags')} hint={t('editor.field.tagsHint')} error={fieldError('tags')}>
              {(p) => <input {...p} className="input" value={form.tags} onChange={(e) => patch('tags', e.target.value)} placeholder="campaign, autumn" />}
            </Field>
            {!isNew && (
              <Field label={t('editor.field.changeNote')} hint={t('editor.field.changeNoteHint')} optionalText={t('label.optional')}>
                {(p) => <input {...p} className="input" value={form.change_note} onChange={(e) => patch('change_note', e.target.value)} />}
              </Field>
            )}
          </div>
        </section>

        <div className="row row-end">
          {submitted && (problems.length > 0 || missingDefault) && (
            <span className="error small" role="alert">
              {t('editor.fixErrors')}
            </span>
          )}
          <button type="submit" className="btn btn-primary" disabled={saving}>
            {saving ? t('action.saving') : t('action.save')}
          </button>
        </div>
      </form>

      {!isNew && (
        <section className="card">
          <h2 className="card-title">{t('editor.versions')}</h2>
          {versions.error ? (
            <ErrorNotice error={versions.error} onRetry={versions.reload} compact />
          ) : (
            <ul className="list-plain small">
              {(versions.data?.items ?? []).map((v) => (
                <li key={v.version} className="row" style={{ justifyContent: 'space-between' }}>
                  <span>
                    <strong>{t('editor.version.n', { n: v.version })}</strong> · {formatDate(v.changed_at)}
                    {v.change_note && <span className="muted"> — {v.change_note}</span>}
                  </span>
                  <JsonView value={v.snapshot} />
                </li>
              ))}
              {versions.data && versions.data.items.length === 0 && <li className="muted">{t('state.empty')}</li>}
            </ul>
          )}
        </section>
      )}
    </div>
  );
}

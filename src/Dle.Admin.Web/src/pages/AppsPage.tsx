import { useState } from 'react';
import { apps, domains, type AppResponse, type DomainResponse, type SdkKeyCreatedResponse } from '../api';
import { ConfirmDialog } from '../components/ConfirmDialog';
import { DataTable, type Column } from '../components/DataTable';
import { Drawer } from '../components/Drawer';
import { EmptyState } from '../components/EmptyState';
import { ErrorNotice } from '../components/ErrorNotice';
import { Field } from '../components/Field';
import { PageHeader } from '../components/PageHeader';
import { SecretReveal } from '../components/SecretReveal';
import { StatusPill } from '../components/StatusPill';
import { useToast } from '../components/ToastContext';
import { describeError, parseList } from '../domain/format';
import { useAsync } from '../hooks/useAsync';
import { useI18n } from '../i18n';

interface AppForm {
  platform: 'ios' | 'android';
  bundle_id: string;
  team_id: string;
  fingerprints: string;
  play_signing: boolean;
  store_id: string;
  store_url: string;
  custom_scheme: string;
  min_app_version: string;
  app_clip_bundle_id: string;
  domain_ids: string[];
}

const EMPTY_FORM: AppForm = {
  platform: 'ios',
  bundle_id: '',
  team_id: '',
  fingerprints: '',
  play_signing: false,
  store_id: '',
  store_url: '',
  custom_scheme: '',
  min_app_version: '',
  app_clip_bundle_id: '',
  domain_ids: [],
};

function formFrom(app: AppResponse): AppForm {
  return {
    platform: app.platform === 'android' ? 'android' : 'ios',
    bundle_id: app.bundle_id,
    team_id: app.team_id ?? '',
    fingerprints: app.cert_fingerprints.join('\n'),
    play_signing: false,
    store_id: app.store_id ?? '',
    store_url: app.store_url ?? '',
    custom_scheme: app.custom_scheme ?? '',
    min_app_version: '',
    app_clip_bundle_id: app.app_clip_bundle_id ?? '',
    domain_ids: app.domain_ids,
  };
}

export default function AppsPage() {
  const { t, formatDate } = useI18n();
  const toast = useToast();
  const list = useAsync((signal) => apps.list(signal), []);
  const domainList = useAsync((signal) => domains.list(signal), []);
  const hostOf = (id: string) => domainList.data?.items.find((d) => d.id === id)?.host ?? id;

  const [creating, setCreating] = useState(false);
  const [managing, setManaging] = useState<AppResponse | null>(null);
  const [deleting, setDeleting] = useState<AppResponse | null>(null);
  const [busy, setBusy] = useState(false);

  const remove = async () => {
    if (!deleting) return;
    setBusy(true);
    try {
      await apps.remove(deleting.id);
      toast.success(t('apps.deleted'));
      setDeleting(null);
      list.reload();
    } catch (error) {
      toast.error(describeError(t, error));
    } finally {
      setBusy(false);
    }
  };

  const columns: Column<AppResponse>[] = [
    {
      key: 'app',
      header: t('apps.col.app'),
      render: (row) => (
        <span className="stack" style={{ gap: 2 }}>
          <span className="row" style={{ gap: 6 }}>
            <StatusPill tone="neutral">{row.platform === 'ios' ? t('platform.ios') : t('platform.android')}</StatusPill>
            <span className="mono" style={{ fontWeight: 600 }}>
              {row.bundle_id}
            </span>
          </span>
          {row.warnings.length > 0 && (
            <span className="row" style={{ gap: 4 }}>
              <StatusPill tone="warn" title={row.warnings.join(' ')}>
                {row.warnings.length} ⚠
              </StatusPill>
            </span>
          )}
        </span>
      ),
    },
    {
      key: 'identity',
      header: t('apps.col.identity'),
      optional: true,
      render: (row) =>
        row.platform === 'ios' ? (
          <span className="mono small">{row.team_id ? `${row.team_id}.${row.bundle_id}` : '—'}</span>
        ) : (
          <span className="small">
            {row.cert_fingerprints.length} {t('apps.col.fingerprints').toLowerCase()}
          </span>
        ),
    },
    {
      key: 'domains',
      header: t('apps.col.domains'),
      optional: true,
      render: (row) => (
        <span className="row" style={{ gap: 4 }}>
          {row.domain_ids.map((id) => (
            <span key={id} className="tag mono">
              {hostOf(id)}
            </span>
          ))}
          {row.domain_ids.length === 0 && <span className="faint">—</span>}
        </span>
      ),
    },
    { key: 'created', header: t('label.created'), optional: true, width: '1%', render: (row) => <span className="small muted" style={{ whiteSpace: 'nowrap' }}>{formatDate(row.created_at)}</span> },
    {
      key: 'actions',
      header: <span className="sr-only">{t('label.actions')}</span>,
      width: '1%',
      align: 'right',
      render: (row) => (
        <span className="row" style={{ gap: 4, justifyContent: 'flex-end', flexWrap: 'nowrap' }}>
          <button type="button" className="btn btn-sm" onClick={() => setManaging(row)}>
            {t('apps.manage')}
          </button>
          <button type="button" className="btn btn-sm btn-ghost" onClick={() => setDeleting(row)}>
            {t('action.delete')}
          </button>
        </span>
      ),
    },
  ];

  return (
    <div className="page">
      <PageHeader
        title={t('apps.title')}
        subtitle={t('apps.subtitle')}
        actions={
          <button type="button" className="btn btn-primary" onClick={() => setCreating(true)}>
            {t('apps.add')}
          </button>
        }
      />

      {list.error ? <ErrorNotice error={list.error} onRetry={list.reload} /> : null}

      <DataTable
        columns={columns}
        rows={list.data?.items}
        rowKey={(row) => row.id}
        caption={t('apps.title')}
        loading={list.loading}
        empty={
          <EmptyState
            title={t('apps.empty.title')}
            detail={t('apps.empty.detail')}
            action={
              <button type="button" className="btn btn-primary" onClick={() => setCreating(true)}>
                {t('apps.add')}
              </button>
            }
          />
        }
      />

      <AppDrawer
        open={creating || managing !== null}
        app={managing}
        domainOptions={domainList.data?.items ?? []}
        onClose={() => {
          setCreating(false);
          setManaging(null);
        }}
        onSaved={() => {
          setCreating(false);
          setManaging(null);
          list.reload();
        }}
      />

      <ConfirmDialog
        open={deleting !== null}
        danger
        busy={busy}
        title={t('apps.delete.title')}
        detail={t('apps.delete.detail')}
        confirmLabel={t('action.delete')}
        typeToConfirm={deleting?.bundle_id}
        onConfirm={remove}
        onCancel={() => setDeleting(null)}
      />
    </div>
  );
}

function AppDrawer({ open, app, domainOptions, onClose, onSaved }: { open: boolean; app: AppResponse | null; domainOptions: DomainResponse[]; onClose: () => void; onSaved: () => void }) {
  const { t } = useI18n();
  const toast = useToast();
  const [form, setForm] = useState<AppForm>(EMPTY_FORM);
  const [loadedFor, setLoadedFor] = useState<string | null | undefined>(undefined);
  const [error, setError] = useState<unknown>(undefined);
  const [busy, setBusy] = useState(false);
  const [warnings, setWarnings] = useState<string[]>([]);

  const key = open ? (app?.id ?? null) : undefined;
  if (key !== loadedFor) {
    setLoadedFor(key);
    setError(undefined);
    setForm(app ? formFrom(app) : EMPTY_FORM);
    setWarnings(app?.warnings ?? []);
  }

  const android = form.platform === 'android';
  const fieldError = (name: string) => (error && typeof error === 'object' && 'fieldError' in error ? (error as { fieldError: (p: string) => string | undefined }).fieldError(name) : undefined);

  const save = async () => {
    setBusy(true);
    setError(undefined);
    try {
      const fingerprints = parseList(form.fingerprints).map((f) => f.toUpperCase());
      let saved: AppResponse;
      if (app) {
        saved = await apps.update(app.id, {
          team_id: android ? null : form.team_id.trim() || null,
          cert_fingerprints: android ? fingerprints : null,
          play_signing_fingerprints: android && form.play_signing ? fingerprints : null,
          store_id: form.store_id.trim() || null,
          store_url: form.store_url.trim() || null,
          custom_scheme: form.custom_scheme.trim() || null,
          min_app_version: form.min_app_version.trim() || null,
          app_clip_bundle_id: android ? null : form.app_clip_bundle_id.trim() || null,
          domain_ids: form.domain_ids,
        });
        toast.success(t('apps.updated'));
      } else {
        saved = await apps.create({
          platform: form.platform,
          bundle_id: form.bundle_id.trim(),
          team_id: android ? null : form.team_id.trim() || null,
          cert_fingerprints: android ? fingerprints : [],
          store_id: form.store_id.trim() || null,
          store_url: form.store_url.trim() || null,
          custom_scheme: form.custom_scheme.trim() || null,
          min_app_version: form.min_app_version.trim() || null,
          app_clip_bundle_id: android ? null : form.app_clip_bundle_id.trim() || null,
          domain_ids: form.domain_ids,
        });
        if (android && form.play_signing && fingerprints.length > 0) {
          saved = await apps.update(saved.id, { play_signing_fingerprints: fingerprints });
        }
        toast.success(t('apps.created'));
      }
      setWarnings(saved.warnings);
      if (saved.warnings.length === 0) {
        onSaved();
      }
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Drawer
      open={open}
      title={app ? `${t('apps.manage')} · ${app.bundle_id}` : t('apps.add')}
      onClose={onClose}
      wide
      footer={
        <>
          <button type="button" className="btn" onClick={onClose} disabled={busy}>
            {t('action.cancel')}
          </button>
          <button type="button" className="btn btn-primary" onClick={() => void save()} disabled={busy || (!app && !form.bundle_id.trim())}>
            {busy ? t('action.saving') : app ? t('action.save') : t('action.create')}
          </button>
        </>
      }
    >
      <form
        className="stack"
        onSubmit={(e) => {
          e.preventDefault();
          void save();
        }}
      >
        {error !== undefined && <ErrorNotice error={error} compact />}
        {warnings.length > 0 && (
          <div className="callout callout-warn" role="alert">
            <strong>{t('apps.warnings')}</strong>
            <ul style={{ margin: 0, paddingLeft: '1.2em' }}>
              {warnings.map((w) => (
                <li key={w}>{w}</li>
              ))}
            </ul>
          </div>
        )}

        <fieldset>
          <legend>{t('apps.field.platform')}</legend>
          <div className="checkbox-grid">
            {(['ios', 'android'] as const).map((platform) => (
              <label key={platform} className="checkbox">
                <input type="radio" name="platform" value={platform} checked={form.platform === platform} disabled={app !== null} onChange={() => setForm({ ...form, platform })} />
                {t(`platform.${platform}`)}
              </label>
            ))}
          </div>
        </fieldset>

        <div className="form-grid">
          <Field label={android ? t('apps.field.packageName') : t('apps.field.bundleId')} error={fieldError('bundle_id')} className="span-2">
            {(p) => <input {...p} className="input mono" value={form.bundle_id} onChange={(e) => setForm({ ...form, bundle_id: e.target.value })} disabled={app !== null} required placeholder="sk.customer.app" data-autofocus autoComplete="off" spellCheck={false} />}
          </Field>
          {!android && (
            <Field label={t('apps.field.teamId')} hint={t('apps.field.teamIdHint')} error={fieldError('team_id')}>
              {(p) => <input {...p} className="input mono" value={form.team_id} maxLength={10} onChange={(e) => setForm({ ...form, team_id: e.target.value.toUpperCase() })} placeholder="ABCDE12345" autoComplete="off" spellCheck={false} />}
            </Field>
          )}
          {!android && (
            <Field label={t('apps.field.appClip')} error={fieldError('app_clip_bundle_id')} optionalText={t('label.optional')}>
              {(p) => <input {...p} className="input mono" value={form.app_clip_bundle_id} onChange={(e) => setForm({ ...form, app_clip_bundle_id: e.target.value })} />}
            </Field>
          )}
        </div>

        {android && (
          <div className="stack">
            <div className="callout callout-warn" role="note">
              <strong>{t('apps.fingerprint.warning.title')}</strong>
              <span>{t('apps.fingerprint.warning.detail')}</span>
            </div>
            <Field label={t('apps.field.fingerprints')} hint={t('apps.field.fingerprintsHint')} error={fieldError('cert_fingerprints')}>
              {(p) => (
                <textarea
                  {...p}
                  className="textarea mono"
                  rows={3}
                  value={form.fingerprints}
                  onChange={(e) => setForm({ ...form, fingerprints: e.target.value })}
                  placeholder="AB:CD:EF:…"
                  spellCheck={false}
                />
              )}
            </Field>
            <label className="checkbox">
              <input type="checkbox" checked={form.play_signing} onChange={(e) => setForm({ ...form, play_signing: e.target.checked })} />
              {t('apps.field.playSigning')}
            </label>
          </div>
        )}

        <div className="form-grid">
          <Field label={t('apps.field.storeId')} error={fieldError('store_id')} optionalText={t('label.optional')}>
            {(p) => <input {...p} className="input mono" value={form.store_id} onChange={(e) => setForm({ ...form, store_id: e.target.value })} placeholder={android ? 'sk.customer.app' : 'id123456789'} />}
          </Field>
          <Field label={t('apps.field.storeUrl')} error={fieldError('store_url')} optionalText={t('label.optional')}>
            {(p) => <input {...p} className="input mono" type="url" value={form.store_url} onChange={(e) => setForm({ ...form, store_url: e.target.value })} placeholder="https://" />}
          </Field>
          <Field label={t('apps.field.customScheme')} hint={t('apps.field.customSchemeHint')} error={fieldError('custom_scheme')} optionalText={t('label.optional')}>
            {(p) => <input {...p} className="input mono" value={form.custom_scheme} onChange={(e) => setForm({ ...form, custom_scheme: e.target.value })} placeholder="myapp" />}
          </Field>
          <Field label={t('apps.field.minAppVersion')} error={fieldError('min_app_version')} optionalText={t('label.optional')}>
            {(p) => <input {...p} className="input mono" value={form.min_app_version} onChange={(e) => setForm({ ...form, min_app_version: e.target.value })} placeholder="1.0.0" />}
          </Field>
        </div>

        <fieldset>
          <legend>{t('apps.field.domains')}</legend>
          {domainOptions.length === 0 && <p className="muted small">{t('domains.empty.title')}</p>}
          <div className="checkbox-grid">
            {domainOptions.map((d) => (
              <label key={d.id} className="checkbox">
                <input
                  type="checkbox"
                  checked={form.domain_ids.includes(d.id)}
                  onChange={(e) => setForm({ ...form, domain_ids: e.target.checked ? [...form.domain_ids, d.id] : form.domain_ids.filter((x) => x !== d.id) })}
                />
                <span className="mono">{d.host}</span>
              </label>
            ))}
          </div>
          {fieldError('domain_ids') && (
            <span className="error small" role="alert">
              {fieldError('domain_ids')}
            </span>
          )}
        </fieldset>
      </form>

      {app && <SdkKeysPanel app={app} />}
    </Drawer>
  );
}

function SdkKeysPanel({ app }: { app: AppResponse }) {
  const { t, formatDate } = useI18n();
  const toast = useToast();
  const keys = useAsync(() => apps.sdkKeys.list(app.id), [app.id]);
  const [created, setCreated] = useState<SdkKeyCreatedResponse | null>(null);
  const [revoking, setRevoking] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const create = async () => {
    setBusy(true);
    try {
      setCreated(await apps.sdkKeys.create(app.id));
      keys.reload();
    } catch (error) {
      toast.error(describeError(t, error));
    } finally {
      setBusy(false);
    }
  };

  const revoke = async () => {
    if (!revoking) return;
    setBusy(true);
    try {
      await apps.sdkKeys.revoke(app.id, revoking);
      setRevoking(null);
      keys.reload();
    } catch (error) {
      toast.error(describeError(t, error));
    } finally {
      setBusy(false);
    }
  };

  const revokingKey = keys.data?.items.find((k) => k.id === revoking);

  return (
    <section className="stack" aria-labelledby="sdk-keys-title">
      <hr className="divider" />
      <div className="row" style={{ justifyContent: 'space-between' }}>
        <h3 id="sdk-keys-title">{t('apps.sdkKeys.title')}</h3>
        <button type="button" className="btn btn-sm btn-primary" onClick={() => void create()} disabled={busy}>
          {t('apps.sdkKeys.create')}
        </button>
      </div>
      <p className="small muted">{t('apps.sdkKeys.hint')}</p>
      {created && <SecretReveal title={t('apps.secret.title')} detail={t('apps.secret.detail')} secret={created.secret} meta={[{ label: t('settings.keys.col.prefix'), value: created.prefix }]} />}
      {keys.error ? <ErrorNotice error={keys.error} onRetry={keys.reload} compact /> : null}
      {keys.data && keys.data.items.length === 0 && <p className="muted small">{t('apps.sdkKeys.empty')}</p>}
      {keys.data && keys.data.items.length > 0 && (
        <ul className="list-plain small">
          {keys.data.items.map((key) => (
            <li key={key.id} className="row" style={{ justifyContent: 'space-between' }}>
              <span className="row">
                <code>{key.prefix}…</code>
                <StatusPill tone={key.is_active ? 'ok' : 'neutral'}>{key.is_active ? t('apps.sdkKeys.active') : t('apps.sdkKeys.revoked')}</StatusPill>
                <span className="muted">{formatDate(key.created_at)}</span>
              </span>
              {key.is_active && (
                <button type="button" className="btn btn-sm btn-ghost" onClick={() => setRevoking(key.id)}>
                  {t('action.revoke')}
                </button>
              )}
            </li>
          ))}
        </ul>
      )}
      <ConfirmDialog
        open={revoking !== null}
        danger
        busy={busy}
        title={t('apps.sdkKeys.revoke.title')}
        detail={t('apps.sdkKeys.revoke.detail', { prefix: revokingKey?.prefix ?? '' })}
        confirmLabel={t('action.revoke')}
        onConfirm={revoke}
        onCancel={() => setRevoking(null)}
      />
    </section>
  );
}

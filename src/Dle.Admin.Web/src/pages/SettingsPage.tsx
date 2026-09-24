import { useState } from 'react';
import {
  apiKeys,
  CONSENT_MODES,
  isApiError,
  ROLES,
  setConnection,
  tenants,
  type ApiKeyCreatedResponse,
  type ApiKeyResponse,
  type Connection,
  type ConsentMode,
  type Role,
  type TenantResponse,
} from '../api';
import { ConfirmDialog } from '../components/ConfirmDialog';
import { DataTable, type Column } from '../components/DataTable';
import { Drawer } from '../components/Drawer';
import { EmptyState } from '../components/EmptyState';
import { ErrorNotice } from '../components/ErrorNotice';
import { Field } from '../components/Field';
import { PageHeader } from '../components/PageHeader';
import { SecretReveal } from '../components/SecretReveal';
import { StatusPill, type PillTone } from '../components/StatusPill';
import { useToast } from '../components/ToastContext';
import { describeError, fromLocalInput } from '../domain/format';
import { useAsync, type AsyncState } from '../hooks/useAsync';
import { useConnection } from '../hooks/useConnection';
import { useTheme, type Theme } from '../hooks/useTheme';
import { LOCALES, useI18n, type Locale } from '../i18n';

const THEMES: readonly Theme[] = ['system', 'light', 'dark'];

/** A language picker names each language in itself, so these are endonyms, not translations. */
const LOCALE_NAMES: Record<Locale, string> = { en: 'English', sk: 'Slovenčina' };

const TENANT_STATUS_TONE: Record<string, PillTone> = { active: 'ok', suspended: 'warn', deleted: 'bad' };

function isRole(value: string): value is Role {
  return (ROLES as readonly string[]).includes(value);
}

function isConsentMode(value: string): value is ConsentMode {
  return (CONSENT_MODES as readonly string[]).includes(value);
}

/**
 * Connection, tenant, API keys, appearance and — for an instance operator key — the tenant list.
 * This is the one page the layout renders without a connection, so the connection form comes first
 * and everything that needs a credential is gated on one.
 */
export default function SettingsPage() {
  const { t } = useI18n();
  const connection = useConnection();
  const me = useAsync((signal) => tenants.me(signal), [connection?.apiKey, connection?.baseUrl], connection !== null);

  return (
    <div className="page">
      <PageHeader title={t('settings.title')} subtitle={t('settings.subtitle')} />

      <ConnectionSection connection={connection} me={me} />

      {connection ? (
        <>
          {me.data && <TenantSection tenant={me.data} onUpdated={me.reload} />}
          <ApiKeysSection />
          <InstanceSection />
        </>
      ) : null}

      <AppearanceSection />
    </div>
  );
}

function ConnectionSection({ connection, me }: { connection: Connection | null; me: AsyncState<TenantResponse> }) {
  const { t } = useI18n();
  const [baseUrl, setBaseUrl] = useState(connection?.baseUrl ?? '');
  const [apiKey, setApiKey] = useState(connection?.apiKey ?? '');
  const [remember, setRemember] = useState(connection?.remember ?? false);

  const connect = () => {
    setConnection({ baseUrl: baseUrl.trim(), apiKey: apiKey.trim(), remember });
  };

  const disconnect = () => {
    setConnection(null);
    setApiKey('');
  };

  return (
    <section className="card stack" aria-labelledby="settings-connection">
      <h2 id="settings-connection" className="card-title">
        {t('connection.title')}
      </h2>
      <p className="small muted">{t('connection.description')}</p>

      <form
        className="stack"
        onSubmit={(e) => {
          e.preventDefault();
          connect();
        }}
      >
        <div className="form-grid">
          <Field label={t('connection.baseUrl')} hint={t('connection.baseUrlHint')} optionalText={t('label.optional')}>
            {(p) => <input {...p} className="input mono" type="url" value={baseUrl} onChange={(e) => setBaseUrl(e.target.value)} placeholder="https://control.example.com" autoComplete="off" spellCheck={false} />}
          </Field>
          <Field label={t('connection.apiKey')} hint={t('connection.apiKeyHint')}>
            {(p) => <input {...p} className="input mono" type="password" value={apiKey} onChange={(e) => setApiKey(e.target.value)} required autoComplete="off" spellCheck={false} />}
          </Field>
        </div>
        <label className="checkbox">
          <input type="checkbox" checked={remember} onChange={(e) => setRemember(e.target.checked)} />
          {t('connection.remember')}
        </label>
        <p className="small muted" style={{ margin: 0 }}>
          {t('connection.rememberHint')}
        </p>
        <div className="row">
          <button type="submit" className="btn btn-primary" disabled={!apiKey.trim()}>
            {t('connection.connect')}
          </button>
          {connection && (
            <button type="button" className="btn btn-ghost" onClick={disconnect}>
              {t('action.disconnect')}
            </button>
          )}
        </div>
      </form>

      {connection && me.loading && <p className="small muted">{t('connection.checking')}</p>}
      {connection && me.data && (
        <p className="row">
          <StatusPill tone="ok">{t('connection.connected', { tenant: me.data.name })}</StatusPill>
        </p>
      )}
      {connection && me.error ? <ErrorNotice error={me.error} onRetry={me.reload} compact /> : null}
    </section>
  );
}

function TenantSection({ tenant, onUpdated }: { tenant: TenantResponse; onUpdated: () => void }) {
  const { t } = useI18n();
  const toast = useToast();
  const [name, setName] = useState(tenant.name);
  const [consentMode, setConsentMode] = useState<ConsentMode>(isConsentMode(tenant.consent_mode) ? tenant.consent_mode : 'aggregate_only');
  const [seededFrom, setSeededFrom] = useState(tenant.id);
  const [error, setError] = useState<unknown>(undefined);
  const [busy, setBusy] = useState(false);

  if (seededFrom !== tenant.id) {
    setSeededFrom(tenant.id);
    setName(tenant.name);
    setConsentMode(isConsentMode(tenant.consent_mode) ? tenant.consent_mode : 'aggregate_only');
  }

  const save = async () => {
    setBusy(true);
    setError(undefined);
    try {
      await tenants.update(tenant.id, { name: name.trim(), consent_mode: consentMode });
      toast.success(t('settings.tenant.updated'));
      onUpdated();
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="card stack" aria-labelledby="settings-tenant">
      <h2 id="settings-tenant" className="card-title">
        {t('settings.tenant.title')}
      </h2>
      <form
        className="stack"
        onSubmit={(e) => {
          e.preventDefault();
          void save();
        }}
      >
        {error !== undefined && <ErrorNotice error={error} compact />}
        <div className="form-grid">
          <Field label={t('settings.tenant.slug')}>
            {(p) => <input {...p} className="input mono" value={tenant.slug} readOnly />}
          </Field>
          <Field label={t('label.name')}>
            {(p) => <input {...p} className="input" value={name} onChange={(e) => setName(e.target.value)} required />}
          </Field>
          <Field label={t('settings.tenant.consentMode')} hint={t('settings.tenant.consentHint')} className="span-2">
            {(p) => (
              <select {...p} className="select mono" value={consentMode} onChange={(e) => setConsentMode(e.target.value as ConsentMode)}>
                {CONSENT_MODES.map((mode) => (
                  <option key={mode} value={mode}>
                    {t(`consent.${mode}`)}
                  </option>
                ))}
              </select>
            )}
          </Field>
        </div>
        <div className="row">
          <button type="submit" className="btn btn-primary" disabled={busy || !name.trim()}>
            {busy ? t('action.saving') : t('settings.tenant.update')}
          </button>
        </div>
      </form>
    </section>
  );
}

function ApiKeysSection() {
  const { t, formatDate } = useI18n();
  const toast = useToast();
  const [includeRevoked, setIncludeRevoked] = useState(false);
  const keys = useAsync((signal) => apiKeys.list(includeRevoked, signal), [includeRevoked]);
  const [creating, setCreating] = useState(false);
  const [created, setCreated] = useState<ApiKeyCreatedResponse | null>(null);
  const [revoking, setRevoking] = useState<ApiKeyResponse | null>(null);
  const [busy, setBusy] = useState(false);

  const revoke = async () => {
    if (!revoking) return;
    setBusy(true);
    try {
      await apiKeys.revoke(revoking.id);
      toast.success(t('settings.keys.revokedToast'));
      setRevoking(null);
      keys.reload();
    } catch (error) {
      toast.error(describeError(t, error));
    } finally {
      setBusy(false);
    }
  };

  const columns: Column<ApiKeyResponse>[] = [
    {
      key: 'name',
      header: t('label.name'),
      render: (row) => (
        <span className="stack" style={{ gap: 2 }}>
          <span style={{ fontWeight: 600 }}>{row.name}</span>
          <code className="small">{row.prefix}…</code>
        </span>
      ),
    },
    {
      key: 'role',
      header: t('label.role'),
      width: '1%',
      render: (row) => <StatusPill tone="neutral">{isRole(row.role) ? t(`role.${row.role}`) : row.role}</StatusPill>,
    },
    {
      key: 'lastUsed',
      header: t('settings.keys.col.lastUsed'),
      optional: true,
      render: (row) => <span className="small muted">{formatDate(row.last_used_at)}</span>,
    },
    {
      key: 'expires',
      header: t('settings.keys.col.expires'),
      optional: true,
      render: (row) => <span className="small muted">{formatDate(row.expires_at)}</span>,
    },
    {
      key: 'status',
      header: t('label.status'),
      width: '1%',
      render: (row) => (row.revoked_at ? <StatusPill tone="bad">{t('settings.keys.revoked')}</StatusPill> : null),
    },
    {
      key: 'actions',
      header: <span className="sr-only">{t('label.actions')}</span>,
      width: '1%',
      align: 'right',
      render: (row) =>
        row.revoked_at ? null : (
          <button type="button" className="btn btn-sm btn-ghost" onClick={() => setRevoking(row)}>
            {t('action.revoke')}
          </button>
        ),
    },
  ];

  return (
    <section className="card stack" aria-labelledby="settings-keys">
      <div className="row" style={{ justifyContent: 'space-between' }}>
        <h2 id="settings-keys" className="card-title">
          {t('settings.keys.title')}
        </h2>
        <button type="button" className="btn btn-sm btn-primary" onClick={() => setCreating(true)}>
          {t('settings.keys.create')}
        </button>
      </div>
      <p className="small muted">{t('settings.keys.hint')}</p>

      {created && (
        <SecretReveal
          title={t('settings.keys.secret.title')}
          detail={t('settings.keys.secret.detail')}
          secret={created.secret}
          meta={[
            { label: t('settings.keys.col.prefix'), value: created.prefix },
            { label: t('label.role'), value: isRole(created.role) ? t(`role.${created.role}`) : created.role },
          ]}
        />
      )}

      <label className="checkbox">
        <input type="checkbox" checked={includeRevoked} onChange={(e) => setIncludeRevoked(e.target.checked)} />
        {t('settings.keys.includeRevoked')}
      </label>

      {keys.error ? <ErrorNotice error={keys.error} onRetry={keys.reload} compact /> : null}
      <DataTable
        columns={columns}
        rows={keys.data?.items}
        rowKey={(row) => row.id}
        caption={t('settings.keys.title')}
        loading={keys.loading}
        empty={<EmptyState title={t('settings.keys.empty')} />}
      />

      <CreateKeyDrawer
        open={creating}
        onClose={() => setCreating(false)}
        onCreated={(key) => {
          setCreating(false);
          setCreated(key);
          toast.success(t('settings.keys.created'));
          keys.reload();
        }}
      />

      <ConfirmDialog
        open={revoking !== null}
        danger
        busy={busy}
        title={t('settings.keys.revoke.title', { name: revoking?.name ?? '' })}
        detail={t('settings.keys.revoke.detail')}
        confirmLabel={t('action.revoke')}
        onConfirm={revoke}
        onCancel={() => setRevoking(null)}
      />
    </section>
  );
}

function CreateKeyDrawer({ open, onClose, onCreated }: { open: boolean; onClose: () => void; onCreated: (key: ApiKeyCreatedResponse) => void }) {
  const { t } = useI18n();
  const [name, setName] = useState('');
  const [role, setRole] = useState<Role>('editor');
  const [expires, setExpires] = useState('');
  const [error, setError] = useState<unknown>(undefined);
  const [busy, setBusy] = useState(false);
  const [wasOpen, setWasOpen] = useState(open);

  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setName('');
      setRole('editor');
      setExpires('');
      setError(undefined);
    }
  }

  const save = async () => {
    setBusy(true);
    setError(undefined);
    try {
      onCreated(await apiKeys.create({ name: name.trim(), role, expires_at: fromLocalInput(expires) }));
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Drawer
      open={open}
      title={t('settings.keys.create')}
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn" onClick={onClose} disabled={busy}>
            {t('action.cancel')}
          </button>
          <button type="button" className="btn btn-primary" onClick={() => void save()} disabled={busy || !name.trim()}>
            {busy ? t('action.saving') : t('action.create')}
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
        <Field label={t('settings.keys.name')}>
          {(p) => <input {...p} className="input" value={name} onChange={(e) => setName(e.target.value)} required data-autofocus autoComplete="off" />}
        </Field>
        <Field label={t('settings.keys.role')}>
          {(p) => (
            <select {...p} className="select" value={role} onChange={(e) => setRole(e.target.value as Role)}>
              {ROLES.map((r) => (
                <option key={r} value={r}>
                  {t(`role.${r}`)}
                </option>
              ))}
            </select>
          )}
        </Field>
        <Field label={t('settings.keys.expiresAt')} hint={t('settings.keys.hint')} optionalText={t('label.optional')}>
          {(p) => <input {...p} className="input" type="datetime-local" value={expires} onChange={(e) => setExpires(e.target.value)} />}
        </Field>
      </form>
    </Drawer>
  );
}

function AppearanceSection() {
  const { t, locale, setLocale } = useI18n();
  const [theme, setTheme] = useTheme();

  return (
    <section className="card stack" aria-labelledby="settings-appearance">
      <h2 id="settings-appearance" className="card-title">
        {t('settings.appearance.title')}
      </h2>
      <div className="form-grid">
        <fieldset>
          <legend>{t('label.theme')}</legend>
          <div className="checkbox-grid">
            {THEMES.map((option) => (
              <label key={option} className="checkbox">
                <input type="radio" name="theme" value={option} checked={theme === option} onChange={() => setTheme(option)} />
                {t(`theme.${option}`)}
              </label>
            ))}
          </div>
        </fieldset>
        <fieldset>
          <legend>{t('label.language')}</legend>
          <div className="checkbox-grid">
            {LOCALES.map((option) => (
              <label key={option} className="checkbox" lang={option}>
                <input type="radio" name="locale" value={option} checked={locale === option} onChange={() => setLocale(option)} />
                {LOCALE_NAMES[option]}
              </label>
            ))}
          </div>
        </fieldset>
      </div>
    </section>
  );
}

function InstanceSection() {
  const { t, formatDate } = useI18n();
  const toast = useToast();
  const [includeDeleted, setIncludeDeleted] = useState(false);
  const list = useAsync((signal) => tenants.list(includeDeleted, signal), [includeDeleted]);
  const [creating, setCreating] = useState(false);

  // A tenant-scoped key gets 403 here; the section is simply not for that operator.
  if (isApiError(list.error) && list.error.status === 403) {
    return null;
  }

  const columns: Column<TenantResponse>[] = [
    { key: 'slug', header: t('settings.instance.slug'), render: (row) => <code>{row.slug}</code> },
    { key: 'name', header: t('settings.instance.name'), render: (row) => <span style={{ fontWeight: 600 }}>{row.name}</span> },
    {
      key: 'status',
      header: t('settings.instance.col.status'),
      width: '1%',
      render: (row) => <StatusPill tone={TENANT_STATUS_TONE[row.status] ?? 'neutral'}>{row.status}</StatusPill>,
    },
    {
      key: 'consent',
      header: t('settings.instance.col.consent'),
      optional: true,
      render: (row) => <code className="small">{isConsentMode(row.consent_mode) ? t(`consent.${row.consent_mode}`) : row.consent_mode}</code>,
    },
    {
      key: 'created',
      header: t('label.created'),
      optional: true,
      width: '1%',
      render: (row) => (
        <span className="small muted" style={{ whiteSpace: 'nowrap' }}>
          {formatDate(row.created_at)}
        </span>
      ),
    },
  ];

  return (
    <section className="card stack" aria-labelledby="settings-instance">
      <div className="row" style={{ justifyContent: 'space-between' }}>
        <h2 id="settings-instance" className="card-title">
          {t('settings.instance.title')}
        </h2>
        <button type="button" className="btn btn-sm btn-primary" onClick={() => setCreating(true)}>
          {t('settings.instance.create')}
        </button>
      </div>
      <p className="small muted">{t('settings.instance.hint')}</p>

      <label className="checkbox">
        <input type="checkbox" checked={includeDeleted} onChange={(e) => setIncludeDeleted(e.target.checked)} />
        {t('settings.instance.includeDeleted')}
      </label>

      {list.error ? <ErrorNotice error={list.error} onRetry={list.reload} compact /> : null}
      <DataTable columns={columns} rows={list.data?.items} rowKey={(row) => row.id} caption={t('settings.instance.title')} loading={list.loading} empty={<EmptyState title={t('state.empty')} />} />

      <CreateTenantDrawer
        open={creating}
        onClose={() => setCreating(false)}
        onCreated={() => {
          setCreating(false);
          toast.success(t('settings.instance.created'));
          list.reload();
        }}
      />
    </section>
  );
}

function CreateTenantDrawer({ open, onClose, onCreated }: { open: boolean; onClose: () => void; onCreated: () => void }) {
  const { t } = useI18n();
  const [slug, setSlug] = useState('');
  const [name, setName] = useState('');
  const [consentMode, setConsentMode] = useState<ConsentMode>('aggregate_only');
  const [error, setError] = useState<unknown>(undefined);
  const [busy, setBusy] = useState(false);
  const [wasOpen, setWasOpen] = useState(open);

  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setSlug('');
      setName('');
      setConsentMode('aggregate_only');
      setError(undefined);
    }
  }

  const save = async () => {
    setBusy(true);
    setError(undefined);
    try {
      await tenants.create({ slug: slug.trim(), name: name.trim(), consent_mode: consentMode });
      onCreated();
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Drawer
      open={open}
      title={t('settings.instance.create')}
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn" onClick={onClose} disabled={busy}>
            {t('action.cancel')}
          </button>
          <button type="button" className="btn btn-primary" onClick={() => void save()} disabled={busy || !slug.trim() || !name.trim()}>
            {busy ? t('action.saving') : t('action.create')}
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
        <Field label={t('settings.instance.slug')}>
          {(p) => <input {...p} className="input mono" value={slug} onChange={(e) => setSlug(e.target.value)} required data-autofocus autoComplete="off" spellCheck={false} />}
        </Field>
        <Field label={t('settings.instance.name')}>
          {(p) => <input {...p} className="input" value={name} onChange={(e) => setName(e.target.value)} required />}
        </Field>
        <Field label={t('settings.tenant.consentMode')} hint={t('settings.tenant.consentHint')}>
          {(p) => (
            <select {...p} className="select mono" value={consentMode} onChange={(e) => setConsentMode(e.target.value as ConsentMode)}>
              {CONSENT_MODES.map((mode) => (
                <option key={mode} value={mode}>
                  {t(`consent.${mode}`)}
                </option>
              ))}
            </select>
          )}
        </Field>
      </form>
    </Drawer>
  );
}

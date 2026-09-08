import { useState } from 'react';
import { CONSENT_MODES, domains, type DomainResponse, type DomainVerificationResponse } from '../api';
import { ConfirmDialog } from '../components/ConfirmDialog';
import { CopyButton } from '../components/CopyButton';
import { DataTable, type Column } from '../components/DataTable';
import { Drawer } from '../components/Drawer';
import { EmptyState } from '../components/EmptyState';
import { ErrorNotice } from '../components/ErrorNotice';
import { Field } from '../components/Field';
import { PageHeader } from '../components/PageHeader';
import { StatusPill } from '../components/StatusPill';
import { toneOfVerification } from '../components/statusTone';
import { useToast } from '../components/ToastContext';
import { describeError } from '../domain/format';
import { useAsync } from '../hooks/useAsync';
import { useI18n, type MessageKey } from '../i18n';

function statusKey(status: string): MessageKey {
  switch (status) {
    case 'ok':
      return 'domains.status.ok';
    case 'failed':
      return 'domains.status.failed';
    case 'warning':
      return 'domains.status.warning';
    default:
      return 'domains.status.pending';
  }
}

export default function DomainsPage() {
  const { t, formatDate } = useI18n();
  const toast = useToast();
  const list = useAsync((signal) => domains.list(signal), []);

  const [adding, setAdding] = useState(false);
  const [editing, setEditing] = useState<DomainResponse | null>(null);
  const [verifying, setVerifying] = useState<string | null>(null);
  const [verification, setVerification] = useState<{ domain: DomainResponse; result: DomainVerificationResponse } | null>(null);
  const [deleting, setDeleting] = useState<DomainResponse | null>(null);
  const [busy, setBusy] = useState(false);

  const verify = async (domain: DomainResponse) => {
    setVerifying(domain.id);
    try {
      const result = await domains.verify(domain.id);
      setVerification({ domain, result });
      list.reload();
    } catch (error) {
      toast.error(describeError(t, error));
    } finally {
      setVerifying(null);
    }
  };

  const remove = async () => {
    if (!deleting) return;
    setBusy(true);
    try {
      await domains.remove(deleting.id);
      toast.success(t('domains.deleted'));
      setDeleting(null);
      list.reload();
    } catch (error) {
      toast.error(describeError(t, error));
    } finally {
      setBusy(false);
    }
  };

  const pill = (status: string) => <StatusPill tone={toneOfVerification(status)}>{t(statusKey(status))}</StatusPill>;

  const columns: Column<DomainResponse>[] = [
    {
      key: 'host',
      header: t('domains.col.host'),
      render: (row) => (
        <span className="stack" style={{ gap: 2 }}>
          <span className="mono" style={{ fontWeight: 600 }}>
            {row.host}
          </span>
          <span className="row" style={{ gap: 4 }}>
            {row.is_default && <span className="tag">{t('domains.col.default')}</span>}
            {!row.is_active && <span className="tag">{t('links.status.inactive')}</span>}
            {row.consent_mode_override && <span className="tag">{row.consent_mode_override}</span>}
          </span>
        </span>
      ),
    },
    { key: 'tls', header: t('domains.col.tls'), width: '1%', render: (row) => pill(row.tls_status) },
    { key: 'aasa', header: t('domains.col.aasa'), width: '1%', render: (row) => pill(row.aasa_status) },
    { key: 'assetlinks', header: t('domains.col.assetlinks'), width: '1%', render: (row) => pill(row.assetlinks_status) },
    {
      key: 'verified',
      header: t('domains.col.lastVerified'),
      optional: true,
      width: '1%',
      render: (row) => <span className="small muted" style={{ whiteSpace: 'nowrap' }}>{row.last_verified_at ? formatDate(row.last_verified_at) : t('domains.never')}</span>,
    },
    {
      key: 'actions',
      header: <span className="sr-only">{t('label.actions')}</span>,
      width: '1%',
      align: 'right',
      render: (row) => (
        <span className="row" style={{ gap: 4, justifyContent: 'flex-end', flexWrap: 'nowrap' }}>
          <button type="button" className="btn btn-sm btn-primary" onClick={() => void verify(row)} disabled={verifying === row.id}>
            {verifying === row.id ? t('state.loading') : t('action.verifyNow')}
          </button>
          <button type="button" className="btn btn-sm" onClick={() => setEditing(row)}>
            {t('action.edit')}
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
        title={t('domains.title')}
        subtitle={t('domains.subtitle')}
        actions={
          <button type="button" className="btn btn-primary" onClick={() => setAdding(true)}>
            {t('domains.add')}
          </button>
        }
      />

      <div className="callout callout-warn" role="note">
        <strong>{t('domains.propagation.title')}</strong>
        <span>{t('domains.propagation.detail')}</span>
      </div>

      {list.error ? <ErrorNotice error={list.error} onRetry={list.reload} /> : null}

      <DataTable
        columns={columns}
        rows={list.data?.items}
        rowKey={(row) => row.id}
        caption={t('domains.title')}
        loading={list.loading}
        empty={
          <EmptyState
            title={t('domains.empty.title')}
            detail={t('domains.empty.detail')}
            action={
              <button type="button" className="btn btn-primary" onClick={() => setAdding(true)}>
                {t('domains.add')}
              </button>
            }
          />
        }
      />

      <DomainFormDrawer
        open={adding || editing !== null}
        domain={editing}
        onClose={() => {
          setAdding(false);
          setEditing(null);
        }}
        onSaved={() => {
          setAdding(false);
          setEditing(null);
          list.reload();
        }}
      />

      <Drawer open={verification !== null} title={verification ? t('domains.verify.title', { host: verification.domain.host }) : ''} onClose={() => setVerification(null)} wide>
        {verification && <VerificationResult domain={verification.domain} result={verification.result} />}
      </Drawer>

      <ConfirmDialog
        open={deleting !== null}
        danger
        busy={busy}
        title={t('domains.delete.title')}
        detail={deleting ? t('domains.delete.detail', { host: deleting.host }) : undefined}
        confirmLabel={t('action.delete')}
        typeToConfirm={deleting?.host}
        onConfirm={remove}
        onCancel={() => setDeleting(null)}
      />
    </div>
  );
}

function VerificationResult({ domain, result }: { domain: DomainResponse; result: DomainVerificationResponse }) {
  const { t, formatDate } = useI18n();
  const history = useAsync(() => domains.verifications(domain.id), [domain.id, result.checked_at]);
  const wellKnown = [`https://${domain.host}/.well-known/apple-app-site-association`, `https://${domain.host}/.well-known/assetlinks.json`];

  return (
    <div className="stack" style={{ gap: 'var(--sp-4)' }}>
      <div className={result.ok ? 'callout callout-ok' : 'callout callout-bad'} role="status">
        <strong>{result.ok ? t('domains.verify.ok') : t('domains.verify.failed')}</strong>
        <span className="small">{formatDate(result.checked_at)}</span>
      </div>

      <div className="scroll-x">
        <table className="small" style={{ width: '100%', borderCollapse: 'collapse' }}>
          <caption className="sr-only">{t('domains.verify.title', { host: domain.host })}</caption>
          <thead>
            <tr>
              <th scope="col" style={{ textAlign: 'left', padding: '4px 8px 4px 0' }}>
                {t('domains.verify.check')}
              </th>
              <th scope="col" style={{ textAlign: 'left', padding: '4px 8px' }}>
                {t('label.status')}
              </th>
              <th scope="col" style={{ textAlign: 'left', padding: '4px 8px' }}>
                {t('domains.verify.reason')}
              </th>
            </tr>
          </thead>
          <tbody>
            {result.checks.map((check) => (
              <tr key={check.kind}>
                <td style={{ padding: '6px 8px 6px 0', borderTop: '1px solid var(--border)', verticalAlign: 'top' }}>
                  <code>{check.kind}</code>
                </td>
                <td style={{ padding: '6px 8px', borderTop: '1px solid var(--border)', verticalAlign: 'top' }}>
                  <StatusPill tone={toneOfVerification(check.status)}>{t(statusKey(check.status))}</StatusPill>
                </td>
                <td style={{ padding: '6px 8px', borderTop: '1px solid var(--border)', verticalAlign: 'top' }}>
                  <div className="stack" style={{ gap: 2 }}>
                    {check.detail ? <span>{check.detail}</span> : <span className="faint">—</span>}
                    {check.codes.length > 0 && (
                      <span className="row" style={{ gap: 4 }}>
                        {check.codes.map((code) => (
                          <code key={code} className="tag">
                            {code}
                          </code>
                        ))}
                      </span>
                    )}
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <div className="callout callout-warn" role="note">
        <strong>{t('domains.propagation.title')}</strong>
        <span>{result.propagation_notice ?? t('domains.propagation.detail')}</span>
      </div>

      <div className="stack" style={{ gap: 6 }}>
        <h3>{t('domains.verify.wellKnown')}</h3>
        {wellKnown.map((url) => (
          <div key={url} className="row">
            <code className="small" style={{ wordBreak: 'break-all' }}>
              {url}
            </code>
            <CopyButton value={url} />
          </div>
        ))}
      </div>

      <div className="stack" style={{ gap: 6 }}>
        <h3>{t('domains.verify.history')}</h3>
        {history.error ? <ErrorNotice error={history.error} compact /> : null}
        {history.data && history.data.items.length === 0 && <p className="muted small">{t('state.empty')}</p>}
        {history.data && history.data.items.length > 0 && (
          <div className="scroll-x">
            <table className="small" style={{ width: '100%', borderCollapse: 'collapse' }}>
              <caption className="sr-only">{t('domains.verify.history')}</caption>
              <thead>
                <tr>
                  <th scope="col" style={{ textAlign: 'left', padding: '4px 8px 4px 0' }}>
                    {t('label.date')}
                  </th>
                  <th scope="col" style={{ textAlign: 'left', padding: '4px 8px' }}>
                    {t('domains.verify.check')}
                  </th>
                  <th scope="col" style={{ textAlign: 'left', padding: '4px 8px' }}>
                    {t('label.status')}
                  </th>
                  <th scope="col" className="num" style={{ padding: '4px 8px' }}>
                    {t('domains.verify.httpStatus')}
                  </th>
                  <th scope="col" className="num" style={{ padding: '4px 0 4px 8px' }}>
                    {t('domains.verify.redirects')}
                  </th>
                </tr>
              </thead>
              <tbody>
                {history.data.items.map((record, i) => (
                  <tr key={`${record.checked_at}-${record.kind}-${i}`}>
                    <td style={{ padding: '4px 8px 4px 0', borderTop: '1px solid var(--border)', whiteSpace: 'nowrap' }}>{formatDate(record.checked_at)}</td>
                    <td style={{ padding: '4px 8px', borderTop: '1px solid var(--border)' }}>
                      <code>{record.kind}</code>
                    </td>
                    <td style={{ padding: '4px 8px', borderTop: '1px solid var(--border)' }}>
                      <StatusPill tone={toneOfVerification(record.status)}>{t(statusKey(record.status))}</StatusPill>
                    </td>
                    <td className="num" style={{ padding: '4px 8px', borderTop: '1px solid var(--border)' }}>
                      {record.http_status ?? '—'}
                    </td>
                    <td className="num" style={{ padding: '4px 0 4px 8px', borderTop: '1px solid var(--border)' }}>
                      {record.redirect_count}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  );
}

function DomainFormDrawer({ open, domain, onClose, onSaved }: { open: boolean; domain: DomainResponse | null; onClose: () => void; onSaved: () => void }) {
  const { t } = useI18n();
  const toast = useToast();
  const [form, setForm] = useState({ host: '', is_default: false, is_active: true, consent_mode_override: '', default_language: 'en' });
  const [loadedFor, setLoadedFor] = useState<string | null | undefined>(undefined);
  const [error, setError] = useState<unknown>(undefined);
  const [busy, setBusy] = useState(false);

  const key = open ? (domain?.id ?? null) : undefined;
  if (key !== loadedFor) {
    setLoadedFor(key);
    setError(undefined);
    setForm(
      domain
        ? { host: domain.host, is_default: domain.is_default, is_active: domain.is_active, consent_mode_override: domain.consent_mode_override ?? '', default_language: 'en' }
        : { host: '', is_default: false, is_active: true, consent_mode_override: '', default_language: 'en' },
    );
  }

  const save = async () => {
    setBusy(true);
    setError(undefined);
    try {
      if (domain) {
        await domains.update(domain.id, {
          is_default: form.is_default,
          is_active: form.is_active,
          consent_mode_override: form.consent_mode_override,
        });
        toast.success(t('domains.updated'));
      } else {
        await domains.create({
          host: form.host.trim().toLowerCase(),
          is_default: form.is_default,
          consent_mode_override: form.consent_mode_override || null,
          default_language: form.default_language || null,
        });
        toast.success(t('domains.created'));
      }
      onSaved();
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  };

  const fieldError = (name: string) => (error && typeof error === 'object' && 'fieldError' in error ? (error as { fieldError: (p: string) => string | undefined }).fieldError(name) : undefined);

  return (
    <Drawer
      open={open}
      title={domain ? `${t('action.edit')} · ${domain.host}` : t('domains.add')}
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn" onClick={onClose} disabled={busy}>
            {t('action.cancel')}
          </button>
          <button type="button" className="btn btn-primary" onClick={() => void save()} disabled={busy || (!domain && !form.host.trim())}>
            {busy ? t('action.saving') : domain ? t('action.save') : t('action.create')}
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
        <Field label={t('domains.field.host')} hint={t('domains.field.hostHint')} error={fieldError('host')}>
          {(p) => <input {...p} className="input mono" value={form.host} onChange={(e) => setForm({ ...form, host: e.target.value })} disabled={domain !== null} required data-autofocus autoComplete="off" spellCheck={false} />}
        </Field>
        <label className="checkbox">
          <input type="checkbox" checked={form.is_default} onChange={(e) => setForm({ ...form, is_default: e.target.checked })} />
          {t('domains.field.isDefault')}
        </label>
        {domain && (
          <label className="checkbox">
            <input type="checkbox" checked={form.is_active} onChange={(e) => setForm({ ...form, is_active: e.target.checked })} />
            {t('domains.field.isActive')}
          </label>
        )}
        <Field label={t('domains.field.consentOverride')} hint={t('domains.field.consentOverrideHint')} error={fieldError('consent_mode_override')}>
          {(p) => (
            <select {...p} className="select" value={form.consent_mode_override} onChange={(e) => setForm({ ...form, consent_mode_override: e.target.value })}>
              <option value="">{t('domains.inherit')}</option>
              {CONSENT_MODES.map((mode) => (
                <option key={mode} value={mode}>
                  {t(`consentMode.${mode}`)}
                </option>
              ))}
            </select>
          )}
        </Field>
        {!domain && (
          <Field label={t('domains.field.defaultLanguage')}>
            {(p) => (
              <select {...p} className="select" value={form.default_language} onChange={(e) => setForm({ ...form, default_language: e.target.value })}>
                <option value="en">EN</option>
                <option value="sk">SK</option>
              </select>
            )}
          </Field>
        )}
      </form>
    </Drawer>
  );
}

import { useState } from 'react';
import { WEBHOOK_EVENT_TYPES, webhooks, type TestWebhookResponse, type WebhookCreatedResponse, type WebhookResponse } from '../api';
import { ConfirmDialog } from '../components/ConfirmDialog';
import { DataTable, type Column } from '../components/DataTable';
import { Drawer } from '../components/Drawer';
import { EmptyState } from '../components/EmptyState';
import { ErrorNotice } from '../components/ErrorNotice';
import { Field } from '../components/Field';
import { JsonView } from '../components/JsonView';
import { PageHeader } from '../components/PageHeader';
import { SecretReveal } from '../components/SecretReveal';
import { StatusPill, type PillTone } from '../components/StatusPill';
import { useToast } from '../components/ToastContext';
import { describeError, shortId } from '../domain/format';
import { useAsync } from '../hooks/useAsync';
import { useI18n, type MessageKey } from '../i18n';

const DELIVERY_TONE: Record<string, PillTone> = { pending: 'pending', delivered: 'ok', failed: 'warn', dead: 'bad' };
const DELIVERY_KEY: Record<string, MessageKey> = {
  pending: 'webhooks.deliveries.status.pending',
  delivered: 'webhooks.deliveries.status.delivered',
  failed: 'webhooks.deliveries.status.failed',
  dead: 'webhooks.deliveries.status.dead',
};

export default function WebhooksPage() {
  const { t, formatDate } = useI18n();
  const toast = useToast();
  const list = useAsync((signal) => webhooks.list(false, signal), []);
  const deliveries = useAsync((signal) => webhooks.deliveries(undefined, 50, false, signal), []);

  const [creating, setCreating] = useState(false);
  const [deleting, setDeleting] = useState<WebhookResponse | null>(null);
  const [testing, setTesting] = useState<string | null>(null);
  const [testResult, setTestResult] = useState<{ hook: WebhookResponse; result: TestWebhookResponse } | null>(null);
  const [busy, setBusy] = useState(false);

  const test = async (hook: WebhookResponse) => {
    setTesting(hook.id);
    try {
      const result = await webhooks.test(hook.id);
      setTestResult({ hook, result });
      deliveries.reload();
    } catch (error) {
      toast.error(describeError(t, error));
    } finally {
      setTesting(null);
    }
  };

  const remove = async () => {
    if (!deleting) return;
    setBusy(true);
    try {
      await webhooks.remove(deleting.id);
      toast.success(t('webhooks.deleted'));
      setDeleting(null);
      list.reload();
    } catch (error) {
      toast.error(describeError(t, error));
    } finally {
      setBusy(false);
    }
  };

  const columns: Column<WebhookResponse>[] = [
    {
      key: 'url',
      header: t('webhooks.col.url'),
      render: (row) => (
        <span className="mono truncate" style={{ maxWidth: 360 }} title={row.url}>
          {row.url}
        </span>
      ),
    },
    {
      key: 'events',
      header: t('webhooks.col.events'),
      optional: true,
      render: (row) => (
        <span className="row" style={{ gap: 4 }}>
          {row.event_types.map((e) => (
            <code key={e} className="tag">
              {e}
            </code>
          ))}
        </span>
      ),
    },
    { key: 'status', header: t('label.status'), width: '1%', render: (row) => <StatusPill tone={row.is_active ? 'ok' : 'neutral'}>{row.is_active ? t('webhooks.active') : t('webhooks.inactive')}</StatusPill> },
    { key: 'created', header: t('label.created'), optional: true, width: '1%', render: (row) => <span className="small muted" style={{ whiteSpace: 'nowrap' }}>{formatDate(row.created_at)}</span> },
    {
      key: 'actions',
      header: <span className="sr-only">{t('label.actions')}</span>,
      width: '1%',
      align: 'right',
      render: (row) => (
        <span className="row" style={{ gap: 4, justifyContent: 'flex-end', flexWrap: 'nowrap' }}>
          <button type="button" className="btn btn-sm" onClick={() => void test(row)} disabled={testing === row.id}>
            {testing === row.id ? t('state.loading') : t('action.test')}
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
        title={t('webhooks.title')}
        subtitle={t('webhooks.subtitle')}
        actions={
          <button type="button" className="btn btn-primary" onClick={() => setCreating(true)}>
            {t('webhooks.add')}
          </button>
        }
      />

      {list.error ? <ErrorNotice error={list.error} onRetry={list.reload} /> : null}

      <DataTable
        columns={columns}
        rows={list.data}
        rowKey={(row) => row.id}
        caption={t('webhooks.title')}
        loading={list.loading}
        empty={
          <EmptyState
            title={t('webhooks.empty.title')}
            detail={t('webhooks.empty.detail')}
            action={
              <button type="button" className="btn btn-primary" onClick={() => setCreating(true)}>
                {t('webhooks.add')}
              </button>
            }
          />
        }
      />

      <section className="card">
        <div className="row" style={{ justifyContent: 'space-between', marginBottom: 'var(--sp-3)' }}>
          <h2 className="card-title" style={{ marginBottom: 0 }}>
            {t('webhooks.deliveries.title')}
          </h2>
          <button type="button" className="btn btn-sm" onClick={deliveries.reload}>
            {t('action.refresh')}
          </button>
        </div>
        {deliveries.error ? <ErrorNotice error={deliveries.error} onRetry={deliveries.reload} compact /> : null}
        <DataTable
          columns={[
            { key: 'event', header: t('webhooks.deliveries.col.event'), render: (d) => <code>{d.event_type}</code> },
            { key: 'sub', header: t('webhooks.col.url'), optional: true, render: (d) => <span className="mono small truncate" style={{ maxWidth: 280 }}>{list.data?.find((h) => h.id === d.subscription_id)?.url ?? shortId(d.subscription_id)}</span> },
            { key: 'status', header: t('label.status'), width: '1%', render: (d) => <StatusPill tone={DELIVERY_TONE[d.status] ?? 'neutral'}>{DELIVERY_KEY[d.status] ? t(DELIVERY_KEY[d.status] as MessageKey) : d.status}</StatusPill> },
            { key: 'attempt', header: t('webhooks.deliveries.col.attempt'), align: 'right', width: '1%', render: (d) => d.attempt },
            {
              key: 'response',
              header: t('webhooks.deliveries.col.response'),
              optional: true,
              render: (d) => (
                <span className="small">
                  {d.response_code ?? '—'}
                  {d.last_error && <span className="muted"> · {d.last_error}</span>}
                </span>
              ),
            },
            { key: 'next', header: t('webhooks.deliveries.col.next'), optional: true, width: '1%', render: (d) => <span className="small muted" style={{ whiteSpace: 'nowrap' }}>{d.next_attempt_at ? formatDate(d.next_attempt_at) : '—'}</span> },
            { key: 'created', header: t('label.created'), width: '1%', render: (d) => <span className="small muted" style={{ whiteSpace: 'nowrap' }}>{formatDate(d.created_at)}</span> },
          ]}
          rows={deliveries.data}
          rowKey={(d) => d.id}
          caption={t('webhooks.deliveries.title')}
          loading={deliveries.loading}
          empty={<span className="muted">{t('webhooks.deliveries.empty')}</span>}
        />
      </section>

      <CreateWebhookDrawer
        open={creating}
        onClose={() => setCreating(false)}
        onCreated={() => {
          list.reload();
        }}
      />

      <Drawer open={testResult !== null} title={t('webhooks.test.title')} onClose={() => setTestResult(null)}>
        {testResult && (
          <div className="stack">
            <div className={testResult.result.delivered ? 'callout callout-ok' : 'callout callout-bad'} role="status">
              <strong>
                {testResult.result.outcome === 'delivered' ? t('webhooks.test.delivered') : testResult.result.outcome === 'unsendable' ? t('webhooks.test.unsendable') : t('webhooks.test.failed')}
              </strong>
              {testResult.result.error && <span>{testResult.result.error}</span>}
            </div>
            <dl className="dl">
              <dt>{t('webhooks.col.url')}</dt>
              <dd className="mono" style={{ wordBreak: 'break-all' }}>
                {testResult.hook.url}
              </dd>
              <dt>{t('webhooks.test.response')}</dt>
              <dd>{testResult.result.response_code ?? '—'}</dd>
              <dt>{t('webhooks.test.elapsed')}</dt>
              <dd>{testResult.result.elapsed_ms} ms</dd>
              {testResult.result.signing_key_id && (
                <>
                  <dt>kid</dt>
                  <dd>
                    <code>{testResult.result.signing_key_id}</code>
                  </dd>
                </>
              )}
            </dl>
            <h3>{t('webhooks.test.payload')}</h3>
            <JsonView value={tryParse(testResult.result.payload)} open />
          </div>
        )}
      </Drawer>

      <ConfirmDialog
        open={deleting !== null}
        danger
        busy={busy}
        title={t('webhooks.delete.title')}
        detail={deleting ? t('webhooks.delete.detail', { url: deleting.url }) : undefined}
        confirmLabel={t('action.delete')}
        onConfirm={remove}
        onCancel={() => setDeleting(null)}
      />
    </div>
  );
}

function tryParse(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

function CreateWebhookDrawer({ open, onClose, onCreated }: { open: boolean; onClose: () => void; onCreated: () => void }) {
  const { t } = useI18n();
  const toast = useToast();
  const [url, setUrl] = useState('');
  const [events, setEvents] = useState<string[]>(['attribution.created']);
  const [error, setError] = useState<unknown>(undefined);
  const [busy, setBusy] = useState(false);
  const [created, setCreated] = useState<WebhookCreatedResponse | null>(null);

  const close = () => {
    setUrl('');
    setEvents(['attribution.created']);
    setError(undefined);
    setCreated(null);
    onClose();
  };

  const save = async () => {
    setBusy(true);
    setError(undefined);
    try {
      const result = await webhooks.create({ url: url.trim(), event_types: events, is_active: true });
      setCreated(result);
      toast.success(t('webhooks.created'));
      onCreated();
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
      title={t('webhooks.add')}
      onClose={close}
      footer={
        created ? (
          <button type="button" className="btn btn-primary" onClick={close}>
            {t('action.close')}
          </button>
        ) : (
          <>
            <button type="button" className="btn" onClick={close} disabled={busy}>
              {t('action.cancel')}
            </button>
            <button type="button" className="btn btn-primary" onClick={() => void save()} disabled={busy || !url.trim() || events.length === 0}>
              {busy ? t('action.saving') : t('action.create')}
            </button>
          </>
        )
      }
    >
      {created ? (
        <div className="stack">
          <SecretReveal
            title={t('webhooks.secret.title')}
            detail={t('webhooks.secret.detail', { kid: created.signing_key_id ?? '—' })}
            secret={created.secret}
            meta={[
              { label: t('label.id'), value: created.id },
              { label: t('webhooks.col.url'), value: created.url },
            ]}
          />
        </div>
      ) : (
        <form
          className="stack"
          onSubmit={(e) => {
            e.preventDefault();
            void save();
          }}
        >
          {error !== undefined && <ErrorNotice error={error} compact />}
          <Field label={t('webhooks.field.url')} hint={t('webhooks.field.urlHint')} error={fieldError('url')}>
            {(p) => <input {...p} className="input mono" type="url" required value={url} onChange={(e) => setUrl(e.target.value)} placeholder="https://" data-autofocus autoComplete="off" spellCheck={false} />}
          </Field>
          <fieldset>
            <legend>{t('webhooks.field.events')}</legend>
            <div className="stack" style={{ gap: 6 }}>
              {WEBHOOK_EVENT_TYPES.map((eventType) => (
                <label key={eventType} className="checkbox">
                  <input type="checkbox" checked={events.includes(eventType)} onChange={(e) => setEvents(e.target.checked ? [...events, eventType] : events.filter((x) => x !== eventType))} />
                  <code>{eventType}</code>
                </label>
              ))}
            </div>
            {fieldError('event_types') && (
              <span className="error small" role="alert">
                {fieldError('event_types')}
              </span>
            )}
          </fieldset>
        </form>
      )}
    </Drawer>
  );
}

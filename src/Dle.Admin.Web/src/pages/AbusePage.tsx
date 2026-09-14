import { useId, useState } from 'react';
import { ABUSE_REASONS, ABUSE_STATUSES, abuse, isApiError, type AbuseReportDetailResponse, type AbuseStatus } from '../api';
import { ConfirmDialog } from '../components/ConfirmDialog';
import { DataTable, type Column } from '../components/DataTable';
import { Drawer } from '../components/Drawer';
import { EmptyState } from '../components/EmptyState';
import { ErrorNotice } from '../components/ErrorNotice';
import { Field } from '../components/Field';
import { PageHeader } from '../components/PageHeader';
import { StatusPill, type PillTone } from '../components/StatusPill';
import { Tabs } from '../components/Tabs';
import { tabPanelProps } from '../components/tabPanelProps';
import { useToast } from '../components/ToastContext';
import { describeError, shortId } from '../domain/format';
import { useAsync } from '../hooks/useAsync';
import { useI18n, type MessageKey } from '../i18n';

type Tab = 'mine' | 'triage' | 'quarantine';

const STATUS_TONE: Record<AbuseStatus, PillTone> = {
  new: 'pending',
  triaged: 'accent',
  confirmed: 'bad',
  rejected: 'neutral',
  resolved: 'ok',
};

function isKnownStatus(status: string): status is AbuseStatus {
  return (ABUSE_STATUSES as readonly string[]).includes(status);
}

function statusKey(status: string): MessageKey {
  return isKnownStatus(status) ? (`abuse.status.${status}` as MessageKey) : 'label.unknown';
}

function reasonKey(reason: string): MessageKey {
  return (ABUSE_REASONS as readonly string[]).includes(reason) ? (`abuse.reason.${reason}` as MessageKey) : 'label.unknown';
}

/**
 * Abuse handling (FR-245, §E.3). Three views: reports filed against the caller's own links, the
 * instance-wide triage queue, and quarantine. Quarantine answers 410 with an explanation rather
 * than deleting the link, because a deleted link destroys the record a later complaint has to be
 * answered from (TC-103) — and the decision notes are the notice-and-action trail DSA art. 16 asks for.
 */
export default function AbusePage() {
  const { t, formatDate } = useI18n();
  const toast = useToast();
  const tabsId = useId();
  const [tab, setTab] = useState<Tab>('mine');
  const [status, setStatus] = useState<string>('');
  const [deciding, setDeciding] = useState<AbuseReportDetailResponse | null>(null);
  const [quarantineLinkId, setQuarantineLinkId] = useState('');

  const mine = useAsync((signal) => abuse.listMine(status || undefined, 100, signal), [status], tab === 'mine');
  const triage = useAsync((signal) => abuse.triageQueue(status || undefined, 100, signal), [status], tab === 'triage');
  const triageForbidden = isApiError(triage.error) && triage.error.status === 403;

  const openQuarantine = (linkId: string) => {
    setQuarantineLinkId(linkId);
    setTab('quarantine');
  };

  const baseColumns: Column<AbuseReportDetailResponse>[] = [
    {
      key: 'report',
      header: t('abuse.col.report'),
      render: (row) => (
        <span className="stack" style={{ gap: 2 }}>
          <code>{shortId(row.id)}</code>
          <span className="small muted">{formatDate(row.created_at)}</span>
        </span>
      ),
    },
    {
      key: 'link',
      header: t('abuse.col.link'),
      render: (row) => <code>{shortId(row.link_id)}</code>,
    },
    {
      key: 'reason',
      header: t('abuse.col.reason'),
      render: (row) => (
        <span className="stack" style={{ gap: 2 }}>
          <span>{t(reasonKey(row.reason))}</span>
          {row.details && <span className="small muted">{row.details}</span>}
        </span>
      ),
    },
    {
      key: 'contact',
      header: t('abuse.col.contact'),
      optional: true,
      render: (row) => <span className="small muted">{row.has_reporter_contact ? t('abuse.hasContact') : t('abuse.noContact')}</span>,
    },
    {
      key: 'status',
      header: t('label.status'),
      width: '1%',
      render: (row) => <StatusPill tone={isKnownStatus(row.status) ? STATUS_TONE[row.status] : 'neutral'}>{t(statusKey(row.status))}</StatusPill>,
    },
    {
      key: 'resolved',
      header: t('abuse.col.resolved'),
      optional: true,
      render: (row) => (
        <span className="stack" style={{ gap: 2 }}>
          <span className="small muted">{formatDate(row.resolved_at)}</span>
          {row.resolution_note && <span className="small">{row.resolution_note}</span>}
        </span>
      ),
    },
  ];

  const triageColumns: Column<AbuseReportDetailResponse>[] = [
    ...baseColumns,
    {
      key: 'actions',
      header: <span className="sr-only">{t('label.actions')}</span>,
      width: '1%',
      align: 'right',
      render: (row) => (
        <span className="row" style={{ gap: 4, justifyContent: 'flex-end', flexWrap: 'nowrap' }}>
          <button type="button" className="btn btn-sm" onClick={() => setDeciding(row)}>
            {t('abuse.decide')}
          </button>
          <button type="button" className="btn btn-sm btn-ghost" onClick={() => openQuarantine(row.link_id)}>
            {t('abuse.tab.quarantine')}
          </button>
        </span>
      ),
    },
  ];

  const statusFilter = (
    <Field label={t('abuse.filter.status')}>
      {(p) => (
        <select {...p} className="select" value={status} onChange={(e) => setStatus(e.target.value)}>
          <option value="">{t('abuse.filter.all')}</option>
          {ABUSE_STATUSES.map((s) => (
            <option key={s} value={s}>
              {t(statusKey(s))}
            </option>
          ))}
        </select>
      )}
    </Field>
  );

  return (
    <div className="page">
      <PageHeader title={t('abuse.title')} subtitle={t('abuse.subtitle')} />

      <Tabs
        id={tabsId}
        label={t('abuse.title')}
        active={tab}
        onChange={(id) => setTab(id as Tab)}
        tabs={[
          { id: 'mine', label: t('abuse.tab.mine') },
          { id: 'triage', label: t('abuse.tab.triage') },
          { id: 'quarantine', label: t('abuse.tab.quarantine') },
        ]}
      />

      {tab === 'mine' && (
        <section {...tabPanelProps(tabsId, 'mine')} className="stack">
          <div className="row" style={{ maxWidth: '18rem' }}>
            {statusFilter}
          </div>
          {mine.error ? <ErrorNotice error={mine.error} onRetry={mine.reload} /> : null}
          <DataTable
            columns={baseColumns}
            rows={mine.data?.items}
            rowKey={(row) => row.id}
            caption={t('abuse.tab.mine')}
            loading={mine.loading}
            empty={<EmptyState title={t('abuse.empty.title')} detail={t('abuse.empty.detail')} />}
          />
        </section>
      )}

      {tab === 'triage' && (
        <section {...tabPanelProps(tabsId, 'triage')} className="stack">
          {triageForbidden ? (
            <EmptyState title={t('state.forbidden')} detail={t('abuse.triageForbidden')} />
          ) : (
            <>
              <div className="row" style={{ maxWidth: '18rem' }}>
                {statusFilter}
              </div>
              {triage.error ? <ErrorNotice error={triage.error} onRetry={triage.reload} /> : null}
              <DataTable
                columns={triageColumns}
                rows={triage.data?.items}
                rowKey={(row) => row.id}
                caption={t('abuse.tab.triage')}
                loading={triage.loading}
                empty={<EmptyState title={t('abuse.empty.title')} detail={t('abuse.empty.detail')} />}
              />
            </>
          )}
        </section>
      )}

      {tab === 'quarantine' && (
        <section {...tabPanelProps(tabsId, 'quarantine')} className="stack">
          <QuarantinePanel
            initialLinkId={quarantineLinkId}
            onDone={() => {
              setQuarantineLinkId('');
              triage.reload();
            }}
          />
        </section>
      )}

      <DecisionDrawer
        report={deciding}
        onClose={() => setDeciding(null)}
        onSaved={() => {
          setDeciding(null);
          toast.success(t('abuse.decided'));
          triage.reload();
          mine.reload();
        }}
      />
    </div>
  );
}

function DecisionDrawer({ report, onClose, onSaved }: { report: AbuseReportDetailResponse | null; onClose: () => void; onSaved: () => void }) {
  const { t } = useI18n();
  const [status, setStatus] = useState<AbuseStatus>('triaged');
  const [note, setNote] = useState('');
  const [loadedFor, setLoadedFor] = useState<string | null | undefined>(undefined);
  const [error, setError] = useState<unknown>(undefined);
  const [busy, setBusy] = useState(false);

  const key = report?.id ?? null;
  if (key !== loadedFor) {
    setLoadedFor(key);
    setError(undefined);
    setNote('');
    setStatus(report && isKnownStatus(report.status) && report.status !== 'new' ? report.status : 'triaged');
  }

  const save = async () => {
    if (!report) return;
    setBusy(true);
    setError(undefined);
    try {
      await abuse.decide(report.id, { status, resolution_note: note.trim() });
      onSaved();
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Drawer
      open={report !== null}
      title={report ? `${t('abuse.decide.title')} · ${shortId(report.id)}` : t('abuse.decide.title')}
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn" onClick={onClose} disabled={busy}>
            {t('action.cancel')}
          </button>
          <button type="button" className="btn btn-primary" onClick={() => void save()} disabled={busy || note.trim().length === 0}>
            {busy ? t('action.saving') : t('action.save')}
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
        <Field label={t('abuse.decide.status')}>
          {(p) => (
            <select {...p} className="select" value={status} onChange={(e) => setStatus(e.target.value as AbuseStatus)} data-autofocus>
              {ABUSE_STATUSES.filter((s) => s !== 'new').map((s) => (
                <option key={s} value={s}>
                  {t(statusKey(s))}
                </option>
              ))}
            </select>
          )}
        </Field>
        <Field label={t('abuse.decide.note')} hint={t('abuse.decide.noteHint')}>
          {(p) => <textarea {...p} className="textarea" rows={5} value={note} onChange={(e) => setNote(e.target.value)} required />}
        </Field>
      </form>
    </Drawer>
  );
}

function QuarantinePanel({ initialLinkId, onDone }: { initialLinkId: string; onDone: () => void }) {
  const { t } = useI18n();
  const toast = useToast();
  const [linkId, setLinkId] = useState(initialLinkId);
  const [seededFrom, setSeededFrom] = useState(initialLinkId);
  const [reason, setReason] = useState('');
  const [releaseId, setReleaseId] = useState('');
  const [releaseReason, setReleaseReason] = useState('');
  const [confirm, setConfirm] = useState<'quarantine' | 'release' | null>(null);
  const [busy, setBusy] = useState(false);

  if (initialLinkId !== seededFrom) {
    setSeededFrom(initialLinkId);
    if (initialLinkId) {
      setLinkId(initialLinkId);
    }
  }

  const run = async () => {
    setBusy(true);
    try {
      if (confirm === 'quarantine') {
        await abuse.quarantine(linkId.trim(), { reason: reason.trim() });
        toast.success(t('abuse.quarantine.done'));
        setReason('');
      } else if (confirm === 'release') {
        await abuse.release(releaseId.trim(), { reason: releaseReason.trim() });
        toast.success(t('abuse.release.done'));
        setReleaseReason('');
      }
      setConfirm(null);
      onDone();
    } catch (error) {
      toast.error(describeError(t, error));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="form-grid">
      <form
        className="stack card"
        onSubmit={(e) => {
          e.preventDefault();
          setConfirm('quarantine');
        }}
      >
        <h2>{t('abuse.quarantine.title')}</h2>
        <div className="callout callout-warn" role="note">
          <span>{t('abuse.quarantine.hint')}</span>
        </div>
        <Field label={t('abuse.quarantine.linkId')}>
          {(p) => <input {...p} className="input mono" value={linkId} onChange={(e) => setLinkId(e.target.value)} required autoComplete="off" spellCheck={false} />}
        </Field>
        <Field label={t('abuse.quarantine.reason')}>
          {(p) => <textarea {...p} className="textarea" rows={3} value={reason} onChange={(e) => setReason(e.target.value)} required />}
        </Field>
        <div className="row">
          <button type="submit" className="btn btn-primary" disabled={busy || !linkId.trim() || !reason.trim()}>
            {t('abuse.tab.quarantine')}
          </button>
        </div>
      </form>

      <form
        className="stack card"
        onSubmit={(e) => {
          e.preventDefault();
          setConfirm('release');
        }}
      >
        <h2>{t('abuse.release.title')}</h2>
        <Field label={t('abuse.quarantine.linkId')}>
          {(p) => <input {...p} className="input mono" value={releaseId} onChange={(e) => setReleaseId(e.target.value)} required autoComplete="off" spellCheck={false} />}
        </Field>
        <Field label={t('abuse.release.reason')}>
          {(p) => <textarea {...p} className="textarea" rows={3} value={releaseReason} onChange={(e) => setReleaseReason(e.target.value)} required />}
        </Field>
        <div className="row">
          <button type="submit" className="btn" disabled={busy || !releaseId.trim() || !releaseReason.trim()}>
            {t('abuse.release.title')}
          </button>
        </div>
      </form>

      <ConfirmDialog
        open={confirm === 'quarantine'}
        danger
        busy={busy}
        title={t('abuse.quarantine.confirmTitle', { id: shortId(linkId.trim()) })}
        detail={t('abuse.quarantine.confirmDetail')}
        confirmLabel={t('abuse.tab.quarantine')}
        onConfirm={run}
        onCancel={() => setConfirm(null)}
      />
      <ConfirmDialog
        open={confirm === 'release'}
        busy={busy}
        title={t('abuse.release.confirmTitle', { id: shortId(releaseId.trim()) })}
        detail={t('abuse.release.confirmDetail')}
        confirmLabel={t('abuse.release.title')}
        onConfirm={run}
        onCancel={() => setConfirm(null)}
      />
    </div>
  );
}

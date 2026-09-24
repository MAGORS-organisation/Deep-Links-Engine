import { useState } from 'react';
import { Link } from 'react-router';
import { domains, links, type LinkResponse } from '../api';
import { ConfirmDialog } from '../components/ConfirmDialog';
import { CopyButton } from '../components/CopyButton';
import { DataTable, type Column } from '../components/DataTable';
import { Drawer } from '../components/Drawer';
import { EmptyState } from '../components/EmptyState';
import { ErrorNotice } from '../components/ErrorNotice';
import { Field } from '../components/Field';
import { PageHeader } from '../components/PageHeader';
import { StatusPill, type PillTone } from '../components/StatusPill';
import { useToast } from '../components/ToastContext';
import { describeError, linkStatus, parseList, type LinkStatus } from '../domain/format';
import { useAsync } from '../hooks/useAsync';
import { useDebouncedValue } from '../hooks/useDebouncedValue';
import { useI18n } from '../i18n';

const PAGE_SIZE = 50;

const STATUS_TONE: Record<LinkStatus, PillTone> = {
  active: 'ok',
  inactive: 'neutral',
  quarantined: 'bad',
  expired: 'warn',
  scheduled: 'pending',
};

export default function LinksPage() {
  const { t, formatDate } = useI18n();
  const toast = useToast();

  const [search, setSearch] = useState('');
  const [tag, setTag] = useState('');
  const [domainId, setDomainId] = useState('');
  const [includeArchived, setIncludeArchived] = useState(false);
  const [cursor, setCursor] = useState<string | undefined>(undefined);
  const [history, setHistory] = useState<(string | undefined)[]>([]);

  const debouncedSearch = useDebouncedValue(search.trim());
  const debouncedTag = useDebouncedValue(tag.trim());

  const domainList = useAsync((signal) => domains.list(signal), []);
  const page = useAsync(
    (signal) =>
      links.list(
        {
          search: debouncedSearch || undefined,
          tags: debouncedTag || undefined,
          domainId: domainId || undefined,
          includeArchived: includeArchived || undefined,
          limit: PAGE_SIZE,
          cursor,
        },
        signal,
      ),
    [debouncedSearch, debouncedTag, domainId, includeArchived, cursor],
  );

  const resetPaging = () => {
    setCursor(undefined);
    setHistory([]);
  };

  const [quick, setQuick] = useState<LinkResponse | null>(null);
  const [qr, setQr] = useState<LinkResponse | null>(null);
  const [confirm, setConfirm] = useState<{ kind: 'archive' | 'delete'; link: LinkResponse } | null>(null);
  const [busy, setBusy] = useState(false);

  const runConfirm = async () => {
    if (!confirm) return;
    setBusy(true);
    try {
      if (confirm.kind === 'archive') {
        await links.archive(confirm.link.id);
        toast.success(t('links.archived'));
      } else {
        await links.remove(confirm.link.id);
        toast.success(t('links.deleted'));
      }
      setConfirm(null);
      page.reload();
    } catch (error) {
      toast.error(describeError(t, error));
    } finally {
      setBusy(false);
    }
  };

  const filtered = Boolean(debouncedSearch || debouncedTag || domainId);

  const columns: Column<LinkResponse>[] = [
    {
      key: 'link',
      header: t('links.col.link'),
      render: (row) => (
        <div className="stack" style={{ gap: 2 }}>
          <span className="row" style={{ gap: 6 }}>
            <Link to={`/links/${row.id}`} className="mono" style={{ fontWeight: 600 }}>
              /{row.slug}
            </Link>
            <CopyButton value={row.short_url} label={t('links.row.copyShort')} className="btn-ghost" />
          </span>
          <span className="small faint truncate" title={row.short_url}>
            {row.host}
          </span>
        </div>
      ),
    },
    {
      key: 'title',
      header: t('links.col.title'),
      render: (row) => (
        <span className="truncate" style={{ maxWidth: 240 }} title={row.title ?? undefined}>
          {row.title ?? <span className="faint">—</span>}
        </span>
      ),
    },
    {
      key: 'target',
      header: t('links.col.target'),
      optional: true,
      render: (row) => (
        <span className="truncate small" style={{ maxWidth: 280 }} title={row.target_url}>
          {row.target_url}
        </span>
      ),
    },
    {
      key: 'tags',
      header: t('links.col.tags'),
      optional: true,
      render: (row) => (
        <span className="row" style={{ gap: 4 }}>
          {row.tags.map((tagName) => (
            <span key={tagName} className="tag">
              {tagName}
            </span>
          ))}
        </span>
      ),
    },
    {
      key: 'rules',
      header: t('links.col.rules'),
      align: 'right',
      width: '1%',
      optional: true,
      render: (row) => row.routing_rules.length,
    },
    {
      key: 'status',
      header: t('label.status'),
      width: '1%',
      render: (row) => {
        const status = linkStatus(row);
        return <StatusPill tone={STATUS_TONE[status]}>{t(`links.status.${status}`)}</StatusPill>;
      },
    },
    {
      key: 'updated',
      header: t('label.updated'),
      optional: true,
      width: '1%',
      render: (row) => <span className="small muted" style={{ whiteSpace: 'nowrap' }}>{formatDate(row.updated_at)}</span>,
    },
    {
      key: 'actions',
      header: <span className="sr-only">{t('label.actions')}</span>,
      width: '1%',
      align: 'right',
      render: (row) => (
        <span className="row" style={{ gap: 4, justifyContent: 'flex-end', flexWrap: 'nowrap' }}>
          <button type="button" className="btn btn-sm" onClick={() => setQuick(row)}>
            {t('links.row.quickEdit')}
          </button>
          <Link className="btn btn-sm" to={`/simulator/${row.id}`}>
            {t('action.simulate')}
          </Link>
          <button type="button" className="btn btn-sm" onClick={() => setQr(row)}>
            {t('links.row.qr')}
          </button>
          {row.is_active && (
            <button type="button" className="btn btn-sm btn-ghost" onClick={() => setConfirm({ kind: 'archive', link: row })}>
              {t('action.archive')}
            </button>
          )}
          <button type="button" className="btn btn-sm btn-ghost" onClick={() => setConfirm({ kind: 'delete', link: row })}>
            {t('action.delete')}
          </button>
        </span>
      ),
    },
  ];

  return (
    <div className="page">
      <PageHeader
        title={t('links.title')}
        subtitle={t('links.subtitle')}
        actions={
          <Link className="btn btn-primary" to="/links/new">
            {t('links.create')}
          </Link>
        }
      />

      <div className="toolbar" role="search">
        <label className="grow field" style={{ gap: 0 }}>
          <span className="sr-only">{t('action.search')}</span>
          <input
            className="input"
            type="search"
            placeholder={t('links.searchPlaceholder')}
            value={search}
            onChange={(e) => {
              setSearch(e.target.value);
              resetPaging();
            }}
          />
        </label>
        <label className="field" style={{ gap: 0, minWidth: 160 }}>
          <span className="sr-only">{t('links.tagFilter')}</span>
          <input
            className="input"
            placeholder={t('links.tagFilterPlaceholder')}
            value={tag}
            onChange={(e) => {
              setTag(e.target.value);
              resetPaging();
            }}
          />
        </label>
        <label className="field" style={{ gap: 0, minWidth: 180 }}>
          <span className="sr-only">{t('links.domainFilter')}</span>
          <select
            className="select"
            value={domainId}
            onChange={(e) => {
              setDomainId(e.target.value);
              resetPaging();
            }}
          >
            <option value="">{t('links.allDomains')}</option>
            {(domainList.data?.items ?? []).map((d) => (
              <option key={d.id} value={d.id}>
                {d.host}
              </option>
            ))}
          </select>
        </label>
        <label className="checkbox">
          <input
            type="checkbox"
            checked={includeArchived}
            onChange={(e) => {
              setIncludeArchived(e.target.checked);
              resetPaging();
            }}
          />
          {t('links.includeArchived')}
        </label>
      </div>

      {page.error ? <ErrorNotice error={page.error} onRetry={page.reload} /> : null}

      <DataTable
        columns={columns}
        rows={page.data?.items}
        rowKey={(row) => row.id}
        caption={t('links.title')}
        loading={page.loading}
        rowClass={(row) => (row.quarantined_at ? 'row-quarantined' : undefined)}
        empty={
          filtered ? (
            <EmptyState title={t('links.empty.filtered')} />
          ) : (
            <EmptyState
              title={t('links.empty.title')}
              detail={t('links.empty.detail')}
              action={
                <Link className="btn btn-primary" to="/links/new">
                  {t('links.create')}
                </Link>
              }
            />
          )
        }
      />

      <nav className="row" aria-label="pagination" style={{ justifyContent: 'space-between' }}>
        <span className="small muted">
          {t('links.page.showing', { count: page.data?.items.length ?? 0 })}{' '}
          {page.data?.total !== null && page.data?.total !== undefined && t('links.page.total', { total: page.data.total })}
        </span>
        <span className="row">
          <button
            type="button"
            className="btn btn-sm"
            disabled={history.length === 0}
            onClick={() => {
              const previous = history[history.length - 1];
              setHistory((h) => h.slice(0, -1));
              setCursor(previous);
            }}
          >
            {t('action.previous')}
          </button>
          <button
            type="button"
            className="btn btn-sm"
            disabled={!page.data?.next_cursor}
            onClick={() => {
              setHistory((h) => [...h, cursor]);
              setCursor(page.data?.next_cursor ?? undefined);
            }}
          >
            {t('action.next')}
          </button>
        </span>
      </nav>

      <QuickEditDrawer
        link={quick}
        onClose={() => setQuick(null)}
        onSaved={() => {
          setQuick(null);
          page.reload();
        }}
      />

      <Drawer open={qr !== null} title={t('links.qr.title')} onClose={() => setQr(null)}>
        {qr && (
          <div className="stack">
            <img
              src={qr.qr_url}
              alt={t('links.qr.alt', { url: qr.short_url })}
              width={240}
              height={240}
              style={{ background: '#fff', borderRadius: 'var(--radius)', border: '1px solid var(--border)', padding: 8 }}
            />
            <p className="small muted">{t('links.qr.hint')}</p>
            <dl className="dl">
              <dt>{t('links.col.link')}</dt>
              <dd className="row">
                <code>{qr.short_url}</code>
                <CopyButton value={qr.short_url} />
              </dd>
              <dt>QR</dt>
              <dd className="row">
                <code>{qr.qr_url}</code>
                <CopyButton value={qr.qr_url} />
              </dd>
            </dl>
          </div>
        )}
      </Drawer>

      <ConfirmDialog
        open={confirm !== null}
        danger
        busy={busy}
        title={confirm?.kind === 'delete' ? t('links.delete.title') : t('links.archive.title')}
        detail={confirm?.kind === 'delete' ? t('links.delete.detail', { slug: confirm.link.slug }) : t('links.archive.detail')}
        confirmLabel={confirm?.kind === 'delete' ? t('action.delete') : t('action.archive')}
        typeToConfirm={confirm?.kind === 'delete' ? confirm.link.slug : undefined}
        onConfirm={runConfirm}
        onCancel={() => setConfirm(null)}
      />
    </div>
  );
}

function QuickEditDrawer({ link, onClose, onSaved }: { link: LinkResponse | null; onClose: () => void; onSaved: () => void }) {
  const { t } = useI18n();
  const toast = useToast();
  const [form, setForm] = useState({ title: '', target_url: '', tags: '', is_active: true, change_note: '' });
  const [loadedFor, setLoadedFor] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(undefined);
  const [busy, setBusy] = useState(false);

  if (link && loadedFor !== link.id) {
    setLoadedFor(link.id);
    setForm({ title: link.title ?? '', target_url: link.target_url, tags: link.tags.join(', '), is_active: link.is_active, change_note: '' });
    setError(undefined);
  }

  const save = async () => {
    if (!link) return;
    setBusy(true);
    setError(undefined);
    try {
      await links.update(link.id, {
        title: form.title.trim() || null,
        target_url: form.target_url.trim(),
        tags: parseList(form.tags),
        is_active: form.is_active,
        change_note: form.change_note.trim() || null,
      });
      toast.success(t('links.saved'));
      onSaved();
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Drawer
      open={link !== null}
      title={t('links.quickEdit.title')}
      onClose={onClose}
      footer={
        <>
          <Link className="btn btn-ghost" to={link ? `/links/${link.id}` : '/links'}>
            {t('links.quickEdit.openFull')}
          </Link>
          <button type="button" className="btn" onClick={onClose} disabled={busy}>
            {t('action.cancel')}
          </button>
          <button type="button" className="btn btn-primary" onClick={() => void save()} disabled={busy}>
            {busy ? t('action.saving') : t('action.save')}
          </button>
        </>
      }
    >
      {link && (
        <form
          className="stack"
          onSubmit={(e) => {
            e.preventDefault();
            void save();
          }}
        >
          <p className="mono muted">{link.short_url}</p>
          {error !== undefined && <ErrorNotice error={error} compact />}
          <Field label={t('editor.field.title')}>
            {(p) => <input {...p} className="input" value={form.title} onChange={(e) => setForm({ ...form, title: e.target.value })} data-autofocus />}
          </Field>
          <Field label={t('editor.field.targetUrl')} error={error instanceof Error && 'fieldError' in error ? (error as { fieldError: (p: string) => string | undefined }).fieldError('target_url') : undefined}>
            {(p) => <input {...p} className="input mono" type="url" required value={form.target_url} onChange={(e) => setForm({ ...form, target_url: e.target.value })} />}
          </Field>
          <Field label={t('editor.field.tags')} hint={t('editor.field.tagsHint')}>
            {(p) => <input {...p} className="input" value={form.tags} onChange={(e) => setForm({ ...form, tags: e.target.value })} />}
          </Field>
          <label className="checkbox">
            <input type="checkbox" checked={form.is_active} onChange={(e) => setForm({ ...form, is_active: e.target.checked })} />
            {t('editor.field.active')}
          </label>
          <Field label={t('editor.field.changeNote')} hint={t('editor.field.changeNoteHint')} optionalText={t('label.optional')}>
            {(p) => <input {...p} className="input" value={form.change_note} onChange={(e) => setForm({ ...form, change_note: e.target.value })} />}
          </Field>
        </form>
      )}
    </Drawer>
  );
}

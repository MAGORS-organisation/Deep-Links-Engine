import { useMemo, useState, type ChangeEvent } from 'react';
import { links, type BulkLinkResult, type DomainResponse } from '../api';
import { Drawer } from '../components/Drawer';
import { ErrorNotice } from '../components/ErrorNotice';
import { Field } from '../components/Field';
import { StatusPill } from '../components/StatusPill';
import { useToast } from '../components/ToastContext';
import { parseFdlOrCsvImport } from '../domain/fdlImport';
import { useI18n } from '../i18n';

interface ImportLinksDrawerProps {
  open: boolean;
  domains: readonly DomainResponse[];
  onClose: () => void;
  onImported: () => void;
}

export function ImportLinksDrawer({ open, domains, onClose, onImported }: ImportLinksDrawerProps) {
  const { t } = useI18n();
  const toast = useToast();

  const defaultDomainId = useMemo(
    () => domains.find((d) => d.is_default)?.id ?? domains[0]?.id ?? '',
    [domains],
  );

  const [domainId, setDomainId] = useState('');
  const [rawText, setRawText] = useState('');
  const [preserveSlugs, setPreserveSlugs] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(undefined);
  const [results, setResults] = useState<BulkLinkResult[]>([]);
  const [wasOpen, setWasOpen] = useState(open);

  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setDomainId(defaultDomainId);
      setError(undefined);
      setResults([]);
    }
  }

  const effectiveDomainId = domainId || defaultDomainId;

  const parsed = useMemo(() => {
    if (!effectiveDomainId || !rawText.trim()) {
      return { items: [], skipped: [] };
    }
    return parseFdlOrCsvImport(rawText, { domainId: effectiveDomainId, preserveSlugs });
  }, [effectiveDomainId, preserveSlugs, rawText]);

  const handleFile = async (e: ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (!file) return;
    const text = await file.text();
    setRawText(text);
    setResults([]);
    setError(undefined);
  };

  const runImport = async () => {
    if (!effectiveDomainId || parsed.items.length === 0) return;
    setBusy(true);
    setError(undefined);
    setResults([]);
    try {
      const collected = await links.bulkCreateNdjson(parsed.items, (line) => {
        setResults((prev) => [...prev, line]);
      });
      const okCount = collected.filter((r) => r.ok).length;
      const failCount = collected.length - okCount;
      toast.success(t('links.import.done', { ok: okCount, failed: failCount }));
      if (okCount > 0) {
        onImported();
      }
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  };

  const okCount = results.filter((r) => r.ok).length;
  const failedResults = results.filter((r) => !r.ok);

  return (
    <Drawer
      open={open}
      wide
      title={t('links.import.title')}
      onClose={() => {
        if (!busy) onClose();
      }}
      footer={
        <>
          <button type="button" className="btn" onClick={onClose} disabled={busy}>
            {t('action.close')}
          </button>
          <button
            type="button"
            className="btn btn-primary"
            disabled={busy || !effectiveDomainId || parsed.items.length === 0}
            onClick={() => void runImport()}
          >
            {busy ? t('links.import.running') : t('links.import.run', { count: parsed.items.length })}
          </button>
        </>
      }
    >
      <div className="stack">
        <p className="small muted" style={{ margin: 0 }}>
          {t('links.import.subtitle')}
        </p>

        {error !== undefined && <ErrorNotice error={error} compact />}

        <Field label={t('links.import.domain')}>
          {(p) => (
            <select
              {...p}
              className="select"
              value={effectiveDomainId}
              onChange={(e) => setDomainId(e.target.value)}
              disabled={busy || domains.length === 0}
            >
              {domains.map((d) => (
                <option key={d.id} value={d.id}>
                  {d.host}
                </option>
              ))}
            </select>
          )}
        </Field>

        <label className="checkbox">
          <input
            type="checkbox"
            checked={preserveSlugs}
            disabled={busy}
            onChange={(e) => setPreserveSlugs(e.target.checked)}
          />
          {t('links.import.preserveSlugs')}
        </label>

        <Field label={t('links.import.file')}>
          {(p) => (
            <input
              {...p}
              className="input"
              type="file"
              accept=".json,.ndjson,.csv,.txt,application/json,text/csv"
              disabled={busy}
              onChange={(e) => void handleFile(e)}
            />
          )}
        </Field>

        <Field label={t('links.import.payload')} hint={t('links.import.payloadHint')}>
          {(p) => (
            <textarea
              {...p}
              className="input mono"
              rows={8}
              value={rawText}
              disabled={busy}
              onChange={(e) => {
                setRawText(e.target.value);
                setResults([]);
              }}
              spellCheck={false}
            />
          )}
        </Field>

        {(parsed.items.length > 0 || parsed.skipped.length > 0) && (
          <div className="row" style={{ gap: 8 }}>
            <StatusPill tone="ok">{t('links.import.parsed', { count: parsed.items.length })}</StatusPill>
            {parsed.skipped.length > 0 && (
              <StatusPill tone="warn">{t('links.import.skippedParse', { count: parsed.skipped.length })}</StatusPill>
            )}
          </div>
        )}

        {parsed.skipped.length > 0 && (
          <ul className="small muted" style={{ margin: 0, paddingLeft: '1.25rem' }}>
            {parsed.skipped.slice(0, 5).map((s) => (
              <li key={`${s.line}-${s.reason}`}>
                #{s.line}: {s.reason}
              </li>
            ))}
          </ul>
        )}

        {results.length > 0 && (
          <div className={failedResults.length === 0 ? 'callout callout-ok' : 'callout callout-warn'} role="status">
            <strong>
              {t('links.import.progress', {
                ok: okCount,
                failed: failedResults.length,
                total: parsed.items.length,
              })}
            </strong>
            {failedResults.length > 0 && (
              <ul className="small" style={{ margin: '4px 0 0', paddingLeft: '1.25rem' }}>
                {failedResults.slice(0, 10).map((r, idx) => (
                  <li key={`${r.ref ?? idx}-${r.error ?? 'err'}`}>
                    <code>{r.ref ?? `#${idx + 1}`}</code>: {r.detail ?? r.error ?? 'Failed'}
                  </li>
                ))}
              </ul>
            )}
          </div>
        )}
      </div>
    </Drawer>
  );
}

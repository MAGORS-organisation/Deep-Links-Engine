import { CopyButton } from './CopyButton';

export interface SecretRevealProps {
  title: string;
  detail: string;
  secret: string;
  meta?: { label: string; value: string }[];
}

/** The one-time display of a freshly created secret. Nothing here is ever fetched again. */
export function SecretReveal({ title, detail, secret, meta }: SecretRevealProps) {
  return (
    <div className="callout callout-warn" role="region" aria-label={title}>
      <strong>{title}</strong>
      <span>{detail}</span>
      <div className="row" style={{ marginTop: 6 }}>
        <code
          style={{
            flex: '1 1 240px',
            padding: '6px 8px',
            background: 'var(--surface)',
            border: '1px solid var(--border-strong)',
            borderRadius: 'var(--radius)',
            wordBreak: 'break-all',
            userSelect: 'all',
          }}
        >
          {secret}
        </code>
        <CopyButton value={secret} small={false} />
      </div>
      {meta && meta.length > 0 && (
        <dl className="dl" style={{ marginTop: 6 }}>
          {meta.map((m) => (
            <div key={m.label} style={{ display: 'contents' }}>
              <dt>{m.label}</dt>
              <dd>
                <code>{m.value}</code>
              </dd>
            </div>
          ))}
        </dl>
      )}
    </div>
  );
}

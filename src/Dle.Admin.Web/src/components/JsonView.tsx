import { useState } from 'react';
import { useT } from '../i18n';
import { CopyButton } from './CopyButton';

export interface JsonViewProps {
  value: unknown;
  /** Start expanded. */
  open?: boolean;
}

export function JsonView({ value, open = false }: JsonViewProps) {
  const t = useT();
  const [shown, setShown] = useState(open);
  const text = typeof value === 'string' ? value : JSON.stringify(value, null, 2);

  return (
    <div className="stack" style={{ gap: 6 }}>
      <div className="row">
        <button type="button" className="btn btn-sm" aria-expanded={shown} onClick={() => setShown((s) => !s)}>
          {shown ? t('action.hideJson') : t('action.showJson')}
        </button>
        {shown && <CopyButton value={text} />}
      </div>
      {shown && (
        <pre
          className="mono"
          style={{
            padding: 'var(--sp-3)',
            background: 'var(--surface-2)',
            border: '1px solid var(--border)',
            borderRadius: 'var(--radius)',
            maxHeight: 360,
            overflow: 'auto',
          }}
        >
          {text}
        </pre>
      )}
    </div>
  );
}

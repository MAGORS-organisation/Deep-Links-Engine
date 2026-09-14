import { useEffect, useState } from 'react';
import { useT } from '../i18n';

export interface CopyButtonProps {
  value: string;
  label?: string;
  small?: boolean;
  className?: string;
}

/**
 * Writes to the clipboard. The console never reads the clipboard (FR-227 applies to the SDK,
 * but the same restraint costs nothing here).
 */
export function CopyButton({ value, label, small = true, className }: CopyButtonProps) {
  const t = useT();
  const [state, setState] = useState<'idle' | 'copied' | 'failed'>('idle');

  useEffect(() => {
    if (state === 'idle') {
      return;
    }
    const handle = window.setTimeout(() => setState('idle'), 1800);
    return () => window.clearTimeout(handle);
  }, [state]);

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(value);
      setState('copied');
    } catch {
      setState('failed');
    }
  };

  const text = state === 'copied' ? t('action.copied') : state === 'failed' ? t('action.copyFailed') : (label ?? t('action.copy'));

  return (
    <button
      type="button"
      className={['btn', small ? 'btn-sm' : '', className ?? ''].filter(Boolean).join(' ')}
      onClick={() => void copy()}
      aria-live="polite"
    >
      {text}
    </button>
  );
}

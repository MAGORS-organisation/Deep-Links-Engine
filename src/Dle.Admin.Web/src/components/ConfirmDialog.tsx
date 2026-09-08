import { useEffect, useId, useRef, useState, type ReactNode } from 'react';
import { useFocusTrap } from '../hooks/useFocusTrap';
import { useT } from '../i18n';
import styles from './ConfirmDialog.module.css';

export interface ConfirmDialogProps {
  open: boolean;
  title: string;
  detail?: ReactNode;
  confirmLabel?: string;
  danger?: boolean;
  /** Ask the operator to type this word before the confirm button enables. */
  typeToConfirm?: string;
  busy?: boolean;
  onConfirm: () => void | Promise<void>;
  onCancel: () => void;
}

/**
 * Every destructive action in the console goes through here. Alert dialog semantics, focus
 * trapped, Escape cancels, and the confirm button is the last thing focus reaches, never the
 * first, so a stray Enter cannot delete anything.
 */
export function ConfirmDialog({
  open,
  title,
  detail,
  confirmLabel,
  danger,
  typeToConfirm,
  busy,
  onConfirm,
  onCancel,
}: ConfirmDialogProps) {
  const t = useT();
  const ref = useRef<HTMLDivElement>(null);
  const titleId = useId();
  const detailId = useId();
  const [typed, setTyped] = useState('');
  useFocusTrap(ref, open, busy ? undefined : onCancel);

  useEffect(() => {
    if (!open) {
      setTyped('');
    }
  }, [open]);

  if (!open) {
    return null;
  }

  const gated = typeToConfirm !== undefined && typed.trim() !== typeToConfirm;

  return (
    <div className={styles.root}>
      <button type="button" className={styles.backdrop} aria-label={t('action.cancel')} onClick={busy ? undefined : onCancel} tabIndex={-1} />
      <div
        ref={ref}
        className={styles.dialog}
        role="alertdialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={detail ? detailId : undefined}
        tabIndex={-1}
      >
        <h2 id={titleId} className={styles.title}>
          {title}
        </h2>
        {detail && (
          <div id={detailId} className={styles.detail}>
            {detail}
          </div>
        )}
        {typeToConfirm !== undefined && (
          <div className="field">
            <label htmlFor={`${titleId}-confirm`}>{t('confirm.typeToConfirm', { word: typeToConfirm })}</label>
            <input
              id={`${titleId}-confirm`}
              className="input mono"
              value={typed}
              onChange={(e) => setTyped(e.target.value)}
              autoComplete="off"
              spellCheck={false}
              data-autofocus
            />
          </div>
        )}
        <div className={styles.actions}>
          <button type="button" className="btn" onClick={onCancel} disabled={busy} data-autofocus={typeToConfirm === undefined ? true : undefined}>
            {t('action.cancel')}
          </button>
          <button
            type="button"
            className={danger ? 'btn btn-danger' : 'btn btn-primary'}
            onClick={() => void onConfirm()}
            disabled={busy || gated}
          >
            {busy ? t('action.saving') : (confirmLabel ?? t('action.confirm'))}
          </button>
        </div>
      </div>
    </div>
  );
}

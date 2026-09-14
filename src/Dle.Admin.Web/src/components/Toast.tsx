import { useCallback, useMemo, useRef, useState, type ReactNode } from 'react';
import { useT } from '../i18n';
import { ToastContext, type ToastApi, type ToastKind, type ToastMessage } from './ToastContext';
import styles from './Toast.module.css';

const AUTO_DISMISS_MS = 6000;

/**
 * Notifications. Success and info land in a polite live region; errors in an assertive one, so a
 * screen reader hears a failed save without the operator having to go looking for it.
 */
export function ToastProvider({ children }: { children: ReactNode }) {
  const t = useT();
  const [toasts, setToasts] = useState<ToastMessage[]>([]);
  const nextId = useRef(1);

  const dismiss = useCallback((id: number) => {
    setToasts((current) => current.filter((toast) => toast.id !== id));
  }, []);

  const push = useCallback(
    (text: string, kind: ToastKind = 'info') => {
      const id = nextId.current++;
      setToasts((current) => [...current, { id, kind, text }]);
      window.setTimeout(() => dismiss(id), kind === 'error' ? AUTO_DISMISS_MS * 2 : AUTO_DISMISS_MS);
    },
    [dismiss],
  );

  const api = useMemo<ToastApi>(
    () => ({
      push,
      success: (text) => push(text, 'success'),
      error: (text) => push(text, 'error'),
      dismiss,
    }),
    [push, dismiss],
  );

  const polite = toasts.filter((toast) => toast.kind !== 'error');
  const assertive = toasts.filter((toast) => toast.kind === 'error');

  return (
    <ToastContext.Provider value={api}>
      {children}
      <div className={styles.region}>
        <div aria-live="polite" aria-atomic="false" className={styles.list}>
          {polite.map((toast) => (
            <ToastItem key={toast.id} toast={toast} onDismiss={dismiss} label={t('toast.dismiss')} />
          ))}
        </div>
        <div role="alert" aria-live="assertive" aria-atomic="false" className={styles.list}>
          {assertive.map((toast) => (
            <ToastItem key={toast.id} toast={toast} onDismiss={dismiss} label={t('toast.dismiss')} />
          ))}
        </div>
      </div>
    </ToastContext.Provider>
  );
}

function ToastItem({ toast, onDismiss, label }: { toast: ToastMessage; onDismiss: (id: number) => void; label: string }) {
  return (
    <div className={[styles.toast, styles[toast.kind] ?? ''].join(' ')}>
      <span className={styles.text}>{toast.text}</span>
      <button type="button" className={styles.close} onClick={() => onDismiss(toast.id)} aria-label={label}>
        ✕
      </button>
    </div>
  );
}

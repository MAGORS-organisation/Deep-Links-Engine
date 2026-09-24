import type { ReactNode } from 'react';
import styles from './EmptyState.module.css';

export interface EmptyStateProps {
  title: string;
  detail?: ReactNode;
  action?: ReactNode;
  tone?: 'neutral' | 'error';
}

export function EmptyState({ title, detail, action, tone = 'neutral' }: EmptyStateProps) {
  return (
    <div className={[styles.root, tone === 'error' ? styles.error : ''].filter(Boolean).join(' ')}>
      <h3 className={styles.title}>{title}</h3>
      {detail && <p className={styles.detail}>{detail}</p>}
      {action && <div className={styles.action}>{action}</div>}
    </div>
  );
}

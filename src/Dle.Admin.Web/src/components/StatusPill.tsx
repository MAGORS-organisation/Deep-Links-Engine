import type { ReactNode } from 'react';
import styles from './StatusPill.module.css';

export type PillTone = 'ok' | 'warn' | 'bad' | 'neutral' | 'pending' | 'accent';

export interface StatusPillProps {
  tone: PillTone;
  children: ReactNode;
  /** Extra screen-reader context, e.g. the failure reason. */
  title?: string;
}

/** Colour is never the only carrier: every pill has a text label and a leading glyph per tone. */
export function StatusPill({ tone, children, title }: StatusPillProps) {
  const glyph = tone === 'ok' ? '●' : tone === 'bad' ? '■' : tone === 'warn' ? '▲' : tone === 'pending' ? '◌' : '○';
  return (
    <span className={[styles.pill, styles[tone] ?? ''].join(' ')} title={title}>
      <span aria-hidden="true" className={styles.glyph}>
        {glyph}
      </span>
      {children}
    </span>
  );
}


import type { ReactNode } from 'react';
import { useT } from '../i18n';
import styles from './DataTable.module.css';

export interface Column<T> {
  key: string;
  header: ReactNode;
  render: (row: T) => ReactNode;
  /** CSS width, e.g. "12rem" or "1%" for shrink-to-fit. */
  width?: string;
  align?: 'left' | 'right';
  /** Hide below 900px. */
  optional?: boolean;
}

export interface DataTableProps<T> {
  columns: Column<T>[];
  rows: readonly T[] | undefined;
  rowKey: (row: T) => string;
  /** Screen-reader caption; visually hidden. Required (WCAG 1.3.1). */
  caption: string;
  loading?: boolean;
  empty?: ReactNode;
  /** Marks a row visually, e.g. selected or quarantined. */
  rowClass?: (row: T) => string | undefined;
}

/**
 * Dense, semantic table. Rows are never clickable as a whole: every action is a real control in
 * the actions column, so keyboard and screen-reader users get exactly what mouse users get.
 */
export function DataTable<T>({ columns, rows, rowKey, caption, loading, empty, rowClass }: DataTableProps<T>) {
  const t = useT();
  const hasRows = rows !== undefined && rows.length > 0;

  return (
    <div className={styles.wrap}>
      <table className={styles.table} aria-busy={loading || undefined}>
        <caption className="sr-only">{caption}</caption>
        <thead>
          <tr>
            {columns.map((column) => (
              <th
                key={column.key}
                scope="col"
                style={column.width ? { width: column.width } : undefined}
                className={[column.align === 'right' ? styles.right : '', column.optional ? styles.optional : '']
                  .filter(Boolean)
                  .join(' ')}
              >
                {column.header}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {hasRows &&
            rows.map((row) => (
              <tr key={rowKey(row)} className={rowClass?.(row)}>
                {columns.map((column) => (
                  <td
                    key={column.key}
                    className={[column.align === 'right' ? styles.right : '', column.optional ? styles.optional : '']
                      .filter(Boolean)
                      .join(' ')}
                  >
                    {column.render(row)}
                  </td>
                ))}
              </tr>
            ))}
          {!hasRows && (
            <tr>
              <td colSpan={columns.length} className={styles.emptyCell}>
                {loading ? <span className="muted">{t('table.loading')}…</span> : (empty ?? <span className="muted">{t('state.empty')}</span>)}
              </td>
            </tr>
          )}
        </tbody>
      </table>
      {loading && hasRows && (
        <div className={styles.loadingBar} aria-hidden="true">
          <span />
        </div>
      )}
    </div>
  );
}

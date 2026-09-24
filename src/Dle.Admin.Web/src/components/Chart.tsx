import { useId } from 'react';
import { useI18n } from '../i18n';
import styles from './Chart.module.css';

/*
 * Inline SVG charts. No library: three shapes cover the whole console, and each one ships with
 * a visually hidden table so the numbers are readable without vision or a pointer.
 */

export interface Series {
  label: string;
  values: number[];
  /** CSS colour, defaults to the chart palette in order. */
  color?: string;
}

export interface LineChartProps {
  labels: string[];
  series: Series[];
  title: string;
  height?: number;
  formatValue?: (value: number) => string;
}

const PALETTE = ['var(--chart-1)', 'var(--chart-2)', 'var(--chart-3)'];

function niceMax(value: number): number {
  if (value <= 0) {
    return 1;
  }
  const magnitude = 10 ** Math.floor(Math.log10(value));
  const normalized = value / magnitude;
  const step = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
  return step * magnitude;
}

export function LineChart({ labels, series, title, height = 220, formatValue }: LineChartProps) {
  const { t, formatNumber } = useI18n();
  const id = useId();
  const fmt = formatValue ?? ((v: number) => formatNumber(v));

  const width = 720;
  const pad = { top: 12, right: 12, bottom: 28, left: 44 };
  const innerW = width - pad.left - pad.right;
  const innerH = height - pad.top - pad.bottom;
  const count = labels.length;
  const max = niceMax(Math.max(0, ...series.flatMap((s) => s.values)));
  const ticks = 4;

  if (count === 0 || series.every((s) => s.values.every((v) => v === 0))) {
    return <p className="muted small">{t('chart.noData')}</p>;
  }

  const x = (i: number) => pad.left + (count === 1 ? innerW / 2 : (i / (count - 1)) * innerW);
  const y = (v: number) => pad.top + innerH - (v / max) * innerH;

  const labelIndexes = count <= 6 ? labels.map((_, i) => i) : [0, Math.floor(count / 2), count - 1];

  return (
    <figure className={styles.figure}>
      <svg viewBox={`0 0 ${width} ${height}`} className={styles.svg} role="img" aria-labelledby={`${id}-title`}>
        <title id={`${id}-title`}>{title}</title>
        {Array.from({ length: ticks + 1 }, (_, i) => {
          const value = (max / ticks) * i;
          return (
            <g key={i}>
              <line x1={pad.left} x2={width - pad.right} y1={y(value)} y2={y(value)} className={styles.grid} />
              <text x={pad.left - 6} y={y(value)} className={styles.tick} textAnchor="end" dominantBaseline="middle">
                {fmt(value)}
              </text>
            </g>
          );
        })}
        {labelIndexes.map((i) => (
          <text key={i} x={x(i)} y={height - 8} className={styles.tick} textAnchor={i === 0 ? 'start' : i === count - 1 ? 'end' : 'middle'}>
            {labels[i]}
          </text>
        ))}
        {series.map((s, si) => {
          const color = s.color ?? PALETTE[si % PALETTE.length];
          const path = s.values.map((v, i) => `${i === 0 ? 'M' : 'L'}${x(i).toFixed(1)},${y(v).toFixed(1)}`).join(' ');
          const area = `${path} L${x(count - 1).toFixed(1)},${(pad.top + innerH).toFixed(1)} L${x(0).toFixed(1)},${(pad.top + innerH).toFixed(1)} Z`;
          return (
            <g key={s.label}>
              {si === 0 && <path d={area} fill={color} opacity={0.08} />}
              <path d={path} fill="none" stroke={color} strokeWidth={2} strokeLinejoin="round" strokeLinecap="round" />
              {count <= 60 &&
                s.values.map((v, i) => (
                  <circle key={i} cx={x(i)} cy={y(v)} r={2.5} fill={color}>
                    <title>{`${labels[i]} — ${t('chart.seriesLabel', { label: s.label, value: fmt(v) })}`}</title>
                  </circle>
                ))}
            </g>
          );
        })}
      </svg>
      <figcaption className={styles.legend}>
        {series.map((s, si) => (
          <span key={s.label} className={styles.legendItem}>
            <span className={styles.swatch} style={{ background: s.color ?? PALETTE[si % PALETTE.length] }} aria-hidden="true" />
            {s.label}
          </span>
        ))}
      </figcaption>
      <table className="sr-only">
        <caption>{title}</caption>
        <thead>
          <tr>
            <th scope="col">—</th>
            {series.map((s) => (
              <th key={s.label} scope="col">
                {s.label}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {labels.map((label, i) => (
            <tr key={label + i}>
              <th scope="row">{label}</th>
              {series.map((s) => (
                <td key={s.label}>{fmt(s.values[i] ?? 0)}</td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </figure>
  );
}

export interface BarRow {
  key: string;
  label: string;
  value: number;
  secondary?: string;
}

export interface BarListProps {
  rows: BarRow[];
  title: string;
  formatValue?: (value: number) => string;
  emptyText?: string;
}

/** Ranked horizontal bars; the shape breakdowns read best in. */
export function BarList({ rows, title, formatValue, emptyText }: BarListProps) {
  const { t, formatNumber } = useI18n();
  const fmt = formatValue ?? ((v: number) => formatNumber(v));
  const max = Math.max(1, ...rows.map((r) => r.value));

  if (rows.length === 0) {
    return <p className="muted small">{emptyText ?? t('chart.noData')}</p>;
  }

  return (
    <table className={styles.bars}>
      <caption className="sr-only">{title}</caption>
      <tbody>
        {rows.map((row) => (
          <tr key={row.key}>
            <th scope="row" className={styles.barLabel}>
              <span className="truncate" title={row.label}>
                {row.label}
              </span>
            </th>
            <td className={styles.barCell}>
              <span className={styles.barTrack} aria-hidden="true">
                <span className={styles.barFill} style={{ width: `${(row.value / max) * 100}%` }} />
              </span>
            </td>
            <td className={styles.barValue}>
              {fmt(row.value)}
              {row.secondary && <span className={styles.barSecondary}> {row.secondary}</span>}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

export interface SplitSegment {
  key: string;
  label: string;
  value: number;
  tone: 'ok' | 'warn' | 'neutral';
}

export interface SplitBarProps {
  segments: SplitSegment[];
  title: string;
}

/** One stacked bar for a composition, with a legend that states each share in text. */
export function SplitBar({ segments, title }: SplitBarProps) {
  const { formatPercent, formatNumber } = useI18n();
  const total = segments.reduce((sum, s) => sum + s.value, 0);
  const id = useId();

  const toneColor = (tone: SplitSegment['tone']) => (tone === 'ok' ? 'var(--ok)' : tone === 'warn' ? 'var(--warn)' : 'var(--chart-3)');
  const tonePattern = (tone: SplitSegment['tone']) => (tone === 'warn' ? `url(#${id}-hatch)` : undefined);

  let offset = 0;
  return (
    <div className={styles.split}>
      <svg viewBox="0 0 100 10" preserveAspectRatio="none" className={styles.splitSvg} role="img" aria-labelledby={`${id}-title`}>
        <title id={`${id}-title`}>{title}</title>
        <defs>
          <pattern id={`${id}-hatch`} patternUnits="userSpaceOnUse" width="1.5" height="1.5" patternTransform="rotate(45)">
            <rect width="1.5" height="1.5" fill="var(--warn)" />
            <line x1="0" y1="0" x2="0" y2="1.5" stroke="var(--surface)" strokeWidth="0.5" />
          </pattern>
        </defs>
        <rect x="0" y="0" width="100" height="10" fill="var(--surface-3)" />
        {total > 0 &&
          segments.map((s) => {
            const w = (s.value / total) * 100;
            const el = <rect key={s.key} x={offset} y="0" width={w} height="10" fill={tonePattern(s.tone) ?? toneColor(s.tone)} />;
            offset += w;
            return el;
          })}
      </svg>
      <ul className={styles.splitLegend}>
        {segments.map((s) => (
          <li key={s.key} className={styles.legendItem}>
            <span
              className={styles.swatch}
              style={{ background: toneColor(s.tone), backgroundImage: s.tone === 'warn' ? 'repeating-linear-gradient(45deg, transparent 0 2px, var(--surface) 2px 3px)' : undefined }}
              aria-hidden="true"
            />
            <span>
              {s.label}: <strong>{total > 0 ? formatPercent(s.value / total) : '—'}</strong>{' '}
              <span className="faint">({formatNumber(s.value)})</span>
            </span>
          </li>
        ))}
      </ul>
    </div>
  );
}

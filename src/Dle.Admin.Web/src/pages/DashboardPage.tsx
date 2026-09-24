import { useMemo, useState } from 'react';
import { analytics, type AnalyticsQuery, type BreakdownDimension, type Grain } from '../api';
import { BarList, LineChart, SplitBar } from '../components/Chart';
import { ErrorNotice } from '../components/ErrorNotice';
import { PageHeader } from '../components/PageHeader';
import { useAsync } from '../hooks/useAsync';
import { useI18n, type MessageKey } from '../i18n';

type Range = '7d' | '30d' | '90d';

const RANGE_DAYS: Record<Range, number> = { '7d': 7, '30d': 30, '90d': 90 };

function windowFor(range: Range): { from: string; to: string } {
  const to = new Date();
  to.setUTCMinutes(0, 0, 0);
  to.setUTCHours(to.getUTCHours() + 1);
  const from = new Date(to);
  from.setUTCDate(from.getUTCDate() - RANGE_DAYS[range]);
  return { from: from.toISOString(), to: to.toISOString() };
}

const MATCH_TYPE_KEYS: Record<string, MessageKey> = {
  install_referrer: 'matchType.install_referrer',
  login: 'matchType.login',
  claim_code: 'matchType.claim_code',
  direct: 'matchType.direct',
  probabilistic: 'matchType.probabilistic',
  none: 'matchType.none',
};

export default function DashboardPage() {
  const { t, formatNumber, formatPercent, locale } = useI18n();
  const [range, setRange] = useState<Range>('30d');
  const [grain, setGrain] = useState<Grain>('day');
  const [includeBots, setIncludeBots] = useState(false);

  const query = useMemo<AnalyticsQuery>(() => ({ ...windowFor(range), grain, include_bots: includeBots, limit: 8 }), [range, grain, includeBots]);
  const deps = [query.from, query.to, query.grain, query.include_bots];

  const series = useAsync((signal) => analytics.clicks(query, signal), deps);
  const quality = useAsync((signal) => analytics.attributionQuality(query, signal), deps);
  const byCountry = useAsync((signal) => analytics.breakdown('country', query, signal), deps);
  const byPlatform = useAsync((signal) => analytics.breakdown('platform', query, signal), deps);
  const byChannel = useAsync((signal) => analytics.breakdown('channel', query, signal), deps);

  const totals = useMemo(() => {
    const points = series.data?.points ?? [];
    const sum = (pick: (p: (typeof points)[number]) => number) => points.reduce((acc, p) => acc + pick(p), 0);
    return (
      series.data?.totals ?? {
        clicks: sum((p) => p.clicks),
        installs: sum((p) => p.installs),
        attributed: 0,
        conversions: sum((p) => p.conversions),
        conversion_rate: 0,
      }
    );
  }, [series.data]);

  const bucketLabel = (iso: string) => {
    const date = new Date(iso);
    if (grain === 'hour') {
      return date.toLocaleTimeString(locale === 'sk' ? 'sk-SK' : 'en-GB', { hour: '2-digit', minute: '2-digit' });
    }
    return date.toLocaleDateString(locale === 'sk' ? 'sk-SK' : 'en-GB', { day: 'numeric', month: 'short' });
  };

  const q = quality.data;
  const installsTotal = q ? q.deterministic + q.probabilistic + q.unmatched : 0;
  const pct = (n: number) => (installsTotal > 0 ? Math.round((n / installsTotal) * 100) : 0);

  const breakdownPanel = (dimension: BreakdownDimension, titleKey: MessageKey, state: typeof byCountry) => (
    <section className="card" aria-labelledby={`bd-${dimension}`}>
      <h2 id={`bd-${dimension}`} className="card-title">
        {t(titleKey)}
      </h2>
      {state.error ? (
        <ErrorNotice error={state.error} onRetry={state.reload} compact />
      ) : (
        <BarList
          title={t(titleKey)}
          rows={(state.data?.rows ?? []).map((row) => ({
            key: row.key,
            label: labelFor(dimension, row.key),
            value: row.clicks,
            secondary: row.installs > 0 ? `· ${formatNumber(row.installs)} ${t('dashboard.kpi.installs').toLowerCase()}` : undefined,
          }))}
          emptyText={state.loading ? t('state.loading') : t('dashboard.breakdown.empty')}
        />
      )}
    </section>
  );

  const labelFor = (dimension: BreakdownDimension, key: string): string => {
    if (dimension === 'platform') {
      const k = `platform.${key}` as MessageKey;
      return key in { ios: 1, android: 1, desktop: 1, other: 1, unknown: 1 } ? t(k) : key;
    }
    if (dimension === 'channel') {
      const k = `channel.${key}` as MessageKey;
      try {
        return t(k) === k ? key : t(k);
      } catch {
        return key;
      }
    }
    return key === 'unknown' || key === '' ? t('label.unknown') : key;
  };

  return (
    <div className="page">
      <PageHeader
        title={t('dashboard.title')}
        subtitle={t('dashboard.subtitle')}
        actions={
          <>
            <label className="field" style={{ gap: 2 }}>
              <span className="sr-only">{t('dashboard.range')}</span>
              <select className="select" value={range} onChange={(e) => setRange(e.target.value as Range)} aria-label={t('dashboard.range')}>
                <option value="7d">{t('dashboard.range.7d')}</option>
                <option value="30d">{t('dashboard.range.30d')}</option>
                <option value="90d">{t('dashboard.range.90d')}</option>
              </select>
            </label>
            <label className="field" style={{ gap: 2 }}>
              <span className="sr-only">{t('dashboard.grain')}</span>
              <select className="select" value={grain} onChange={(e) => setGrain(e.target.value as Grain)} aria-label={t('dashboard.grain')}>
                <option value="hour">{t('dashboard.grain.hour')}</option>
                <option value="day">{t('dashboard.grain.day')}</option>
                <option value="week">{t('dashboard.grain.week')}</option>
                <option value="month">{t('dashboard.grain.month')}</option>
              </select>
            </label>
            <label className="checkbox">
              <input type="checkbox" checked={includeBots} onChange={(e) => setIncludeBots(e.target.checked)} />
              {t('dashboard.includeBots')}
            </label>
          </>
        }
      />

      {series.error ? <ErrorNotice error={series.error} onRetry={series.reload} /> : null}

      <section className="grid-3" aria-label={t('dashboard.title')}>
        <div className="card kpi">
          <span className="kpi-label">{t('dashboard.kpi.clicks')}</span>
          <span className="kpi-value">{series.loading && !series.data ? '…' : formatNumber(totals.clicks)}</span>
        </div>
        <div className="card kpi">
          <span className="kpi-label">{t('dashboard.kpi.installs')}</span>
          <span className="kpi-value">{series.loading && !series.data ? '…' : formatNumber(totals.installs)}</span>
        </div>
        <div className="card kpi">
          <span className="kpi-label">{t('dashboard.kpi.conversions')}</span>
          <span className="kpi-value">{series.loading && !series.data ? '…' : formatNumber(totals.conversions)}</span>
          <span className="small muted">
            {t('dashboard.kpi.rate')}: {formatPercent(totals.conversion_rate > 1 ? totals.conversion_rate / 100 : totals.conversion_rate)}
          </span>
        </div>
      </section>

      <section className="card" aria-labelledby="series-title">
        <h2 id="series-title" className="card-title">
          {t('dashboard.series.title')}
        </h2>
        {series.loading && !series.data ? (
          <p className="muted small">{t('state.loading')}</p>
        ) : (
          <LineChart
            title={t('dashboard.series.title')}
            labels={(series.data?.points ?? []).map((p) => bucketLabel(p.bucket))}
            series={[
              { label: t('dashboard.series.clicks'), values: (series.data?.points ?? []).map((p) => p.clicks) },
              { label: t('dashboard.series.installs'), values: (series.data?.points ?? []).map((p) => p.installs) },
            ]}
          />
        )}
      </section>

      <section className="card" aria-labelledby="attribution-title">
        <h2 id="attribution-title" className="card-title">
          {t('dashboard.attribution.title')}
        </h2>
        <p className="muted small" style={{ marginBottom: 'var(--sp-3)' }}>
          {t('dashboard.attribution.subtitle')}
        </p>
        {quality.error ? (
          <ErrorNotice error={quality.error} onRetry={quality.reload} compact />
        ) : quality.loading && !q ? (
          <p className="muted small">{t('state.loading')}</p>
        ) : !q || installsTotal === 0 ? (
          <p className="muted small">{t('dashboard.attribution.empty')}</p>
        ) : (
          <div className="stack" style={{ gap: 'var(--sp-4)' }}>
            <p style={{ fontSize: 'var(--fs-lg)', fontWeight: 620 }} aria-live="polite">
              {t('dashboard.attribution.sentence', {
                deterministic: pct(q.deterministic),
                probabilistic: pct(q.probabilistic),
                confidence: q.average_probabilistic_confidence.toFixed(2),
                unmatched: pct(q.unmatched),
              })}
            </p>
            <SplitBar
              title={t('dashboard.attribution.title')}
              segments={[
                { key: 'det', label: t('dashboard.attribution.deterministic'), value: q.deterministic, tone: 'ok' },
                { key: 'prob', label: t('dashboard.attribution.probabilistic'), value: q.probabilistic, tone: 'warn' },
                { key: 'un', label: t('dashboard.attribution.unmatched'), value: q.unmatched, tone: 'neutral' },
              ]}
            />
            <div className="grid-3">
              <div className="stack" style={{ gap: 2 }}>
                <strong>{t('dashboard.attribution.deterministic')}</strong>
                <span className="small muted">{t('dashboard.attribution.deterministicHint')}</span>
              </div>
              <div className="stack" style={{ gap: 2 }}>
                <strong>{t('dashboard.attribution.probabilistic')}</strong>
                <span className="small muted">{t('dashboard.attribution.probabilisticHint')}</span>
                <span className="small">
                  {t('dashboard.attribution.avgConfidence')}: <strong>{q.average_probabilistic_confidence.toFixed(2)}</strong>
                </span>
              </div>
              <div className="stack" style={{ gap: 2 }}>
                <strong>{t('dashboard.attribution.unmatched')}</strong>
                <span className="small muted">{t('dashboard.attribution.unmatchedHint')}</span>
              </div>
            </div>
            {q.by_match_type.length > 0 && (
              <div className="scroll-x">
                <table className="small" style={{ borderCollapse: 'collapse', minWidth: 360 }}>
                  <caption className="sr-only">{t('dashboard.attribution.byMatchType')}</caption>
                  <thead>
                    <tr>
                      <th scope="col" style={{ textAlign: 'left', padding: '4px 8px 4px 0' }}>
                        {t('dashboard.attribution.matchType')}
                      </th>
                      <th scope="col" className="num" style={{ padding: '4px 8px' }}>
                        {t('dashboard.attribution.count')}
                      </th>
                      <th scope="col" className="num" style={{ padding: '4px 0 4px 8px' }}>
                        {t('dashboard.attribution.confidence')}
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    {q.by_match_type.map((row) => (
                      <tr key={row.match_type}>
                        <td style={{ padding: '4px 8px 4px 0', borderTop: '1px solid var(--border)' }}>
                          {MATCH_TYPE_KEYS[row.match_type] ? t(MATCH_TYPE_KEYS[row.match_type] as MessageKey) : row.match_type}
                          {row.match_type === 'probabilistic' && <span className="tag" style={{ marginLeft: 6 }}>≠ 1.00</span>}
                        </td>
                        <td className="num" style={{ padding: '4px 8px', borderTop: '1px solid var(--border)' }}>
                          {formatNumber(row.count)}
                        </td>
                        <td className="num" style={{ padding: '4px 0 4px 8px', borderTop: '1px solid var(--border)' }}>
                          {row.average_confidence.toFixed(2)}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            <p className="small faint">{t('dashboard.attribution.total', { total: formatNumber(installsTotal) })}</p>
          </div>
        )}
      </section>

      <div className="grid-3">
        {breakdownPanel('country', 'dashboard.breakdown.country', byCountry)}
        {breakdownPanel('platform', 'dashboard.breakdown.platform', byPlatform)}
        {breakdownPanel('channel', 'dashboard.breakdown.channel', byChannel)}
      </div>
    </div>
  );
}

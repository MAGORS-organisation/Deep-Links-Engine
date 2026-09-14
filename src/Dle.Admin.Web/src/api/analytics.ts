import { api } from './client';
import type {
  AnalyticsQuery,
  AttributionQualityResponse,
  BreakdownDimension,
  BreakdownResponse,
  FunnelSummary,
  TimeSeriesResponse,
} from './types';

const base = '/api/v1/analytics';

/** The analytics binder reads snake_case names from the query string (AnalyticsQueryBinder). */
function toQuery(query: AnalyticsQuery) {
  return {
    from: query.from,
    to: query.to,
    grain: query.grain,
    link_id: query.link_id,
    campaign_id: query.campaign_id,
    country: query.country,
    platform: query.platform,
    include_bots: query.include_bots,
    limit: query.limit,
  };
}

export const analytics = {
  clicks(query: AnalyticsQuery, signal?: AbortSignal): Promise<TimeSeriesResponse> {
    return api(`${base}/clicks`, { query: toQuery(query), signal });
  },

  installs(query: AnalyticsQuery, signal?: AbortSignal): Promise<TimeSeriesResponse> {
    return api(`${base}/installs`, { query: toQuery(query), signal });
  },

  breakdown(dimension: BreakdownDimension, query: AnalyticsQuery, signal?: AbortSignal): Promise<BreakdownResponse> {
    return api(`${base}/breakdown`, { query: { ...toQuery(query), dimension }, signal });
  },

  funnel(query: AnalyticsQuery, signal?: AbortSignal): Promise<FunnelSummary> {
    return api(`${base}/funnels`, { query: toQuery(query), signal });
  },

  /** ADR-008. Deterministic vs probabilistic vs unmatched, with the mean confidence of the guesses. */
  attributionQuality(query: AnalyticsQuery, signal?: AbortSignal): Promise<AttributionQualityResponse> {
    return api(`${base}/attribution-quality`, { query: toQuery(query), signal });
  },
};

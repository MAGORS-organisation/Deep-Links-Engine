import { api } from './client';
import type {
  AbuseReportDetailResponse,
  PagedResponse,
  QuarantineDecisionRequest,
  TriageDecisionRequest,
} from './types';

export const abuse = {
  /** Reports against the caller's own links (dle.links.read). */
  listMine(status?: string, limit?: number, signal?: AbortSignal): Promise<PagedResponse<AbuseReportDetailResponse>> {
    return api('/api/v1/abuse-reports', { query: { status, limit }, signal });
  },

  /** Instance-wide triage queue (dle.tenants.write). 403 for a tenant key, which the page hides. */
  triageQueue(status?: string, limit?: number, signal?: AbortSignal): Promise<PagedResponse<AbuseReportDetailResponse>> {
    return api('/api/v1/admin/abuse-reports', { query: { status, limit }, signal });
  },

  decide(reportId: string, body: TriageDecisionRequest): Promise<void> {
    return api(`/api/v1/admin/abuse-reports/${encodeURIComponent(reportId)}/decision`, {
      method: 'POST',
      body,
    });
  },

  /** TC-103: the link answers 410 from the next resolve on. */
  quarantine(linkId: string, body: QuarantineDecisionRequest): Promise<void> {
    return api(`/api/v1/admin/links/${encodeURIComponent(linkId)}/quarantine`, { method: 'POST', body });
  },

  release(linkId: string, body: QuarantineDecisionRequest): Promise<void> {
    return api(`/api/v1/admin/links/${encodeURIComponent(linkId)}/quarantine/release`, {
      method: 'POST',
      body,
    });
  },
};

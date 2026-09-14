import { api } from './client';
import type {
  CreateDomainRequest,
  DomainResponse,
  DomainVerificationRecord,
  DomainVerificationResponse,
  PagedResponse,
  UpdateDomainRequest,
} from './types';

const base = '/api/v1/domains';

export const domains = {
  list(signal?: AbortSignal): Promise<PagedResponse<DomainResponse>> {
    return api(base, { signal });
  },

  get(id: string): Promise<DomainResponse> {
    return api(`${base}/${encodeURIComponent(id)}`);
  },

  create(body: CreateDomainRequest): Promise<DomainResponse> {
    return api(base, { method: 'POST', body });
  },

  update(id: string, body: UpdateDomainRequest): Promise<DomainResponse> {
    return api(`${base}/${encodeURIComponent(id)}`, { method: 'PATCH', body });
  },

  remove(id: string): Promise<void> {
    return api(`${base}/${encodeURIComponent(id)}`, { method: 'DELETE' });
  },

  /** FR-143. Runs DNS, TLS, AASA and assetlinks checks now and returns each with its reason. */
  verify(id: string): Promise<DomainVerificationResponse> {
    return api(`${base}/${encodeURIComponent(id)}/verify`, { method: 'POST', idempotent: false });
  },

  verifications(id: string): Promise<PagedResponse<DomainVerificationRecord>> {
    return api(`${base}/${encodeURIComponent(id)}/verifications`);
  },
};

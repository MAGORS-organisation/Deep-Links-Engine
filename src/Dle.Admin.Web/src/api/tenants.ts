import { api } from './client';
import type { CreateTenantRequest, PagedResponse, TenantResponse, UpdateTenantRequest } from './types';

const base = '/api/v1/tenants';

export const tenants = {
  /** The caller's own tenant; the one route here every role can read. */
  me(signal?: AbortSignal): Promise<TenantResponse> {
    return api(`${base}/me`, { signal });
  },

  /** Instance operator only (dle.tenants.write); answers 403 for a tenant-scoped key. */
  list(includeDeleted = false, signal?: AbortSignal): Promise<PagedResponse<TenantResponse>> {
    return api(base, { query: { includeDeleted }, signal });
  },

  create(body: CreateTenantRequest): Promise<TenantResponse> {
    return api(base, { method: 'POST', body });
  },

  update(id: string, body: UpdateTenantRequest): Promise<TenantResponse> {
    return api(`${base}/${encodeURIComponent(id)}`, { method: 'PATCH', body });
  },

  remove(id: string): Promise<void> {
    return api(`${base}/${encodeURIComponent(id)}`, { method: 'DELETE' });
  },
};

import { api } from './client';
import type { ApiKeyCreatedResponse, ApiKeyResponse, CreateApiKeyRequest, PagedResponse } from './types';

const base = '/api/v1/api-keys';

export const apiKeys = {
  list(includeRevoked = false, signal?: AbortSignal): Promise<PagedResponse<ApiKeyResponse>> {
    return api(base, { query: { includeRevoked }, signal });
  },

  /** The secret in the answer is shown once and never retrievable again (FR-242). */
  create(body: CreateApiKeyRequest): Promise<ApiKeyCreatedResponse> {
    return api(base, { method: 'POST', body });
  },

  revoke(id: string): Promise<void> {
    return api(`${base}/${encodeURIComponent(id)}`, { method: 'DELETE' });
  },
};

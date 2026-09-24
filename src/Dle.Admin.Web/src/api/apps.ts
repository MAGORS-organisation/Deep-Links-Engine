import { api } from './client';
import type {
  AppResponse,
  CreateAppRequest,
  PagedResponse,
  SdkKeyCreatedResponse,
  SdkKeyResponse,
  UpdateAppRequest,
} from './types';

const base = '/api/v1/apps';

export const apps = {
  list(signal?: AbortSignal): Promise<PagedResponse<AppResponse>> {
    return api(base, { signal });
  },

  get(id: string): Promise<AppResponse> {
    return api(`${base}/${encodeURIComponent(id)}`);
  },

  create(body: CreateAppRequest): Promise<AppResponse> {
    return api(base, { method: 'POST', body });
  },

  update(id: string, body: UpdateAppRequest): Promise<AppResponse> {
    return api(`${base}/${encodeURIComponent(id)}`, { method: 'PATCH', body });
  },

  remove(id: string): Promise<void> {
    return api(`${base}/${encodeURIComponent(id)}`, { method: 'DELETE' });
  },

  sdkKeys: {
    list(appId: string): Promise<PagedResponse<SdkKeyResponse>> {
      return api(`${base}/${encodeURIComponent(appId)}/sdk-keys`);
    },
    create(appId: string): Promise<SdkKeyCreatedResponse> {
      return api(`${base}/${encodeURIComponent(appId)}/sdk-keys`, { method: 'POST' });
    },
    revoke(appId: string, keyId: string): Promise<void> {
      return api(`${base}/${encodeURIComponent(appId)}/sdk-keys/${encodeURIComponent(keyId)}`, {
        method: 'DELETE',
      });
    },
  },
};

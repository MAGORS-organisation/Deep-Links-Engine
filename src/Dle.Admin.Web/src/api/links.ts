import { api } from './client';
import type {
  CreateLinkRequest,
  LinkListQuery,
  LinkResponse,
  LinkTemplateRequest,
  LinkTemplateResponse,
  LinkVersionSummary,
  PagedResponse,
  SimulateRequest,
  SimulateResponse,
  UpdateLinkRequest,
} from './types';

const base = '/api/v1/links';

export const links = {
  list(query: LinkListQuery = {}, signal?: AbortSignal): Promise<PagedResponse<LinkResponse>> {
    return api(base, { query: { ...query }, signal });
  },

  get(id: string, signal?: AbortSignal): Promise<LinkResponse> {
    return api(`${base}/${encodeURIComponent(id)}`, { signal });
  },

  create(body: CreateLinkRequest): Promise<LinkResponse> {
    return api(base, { method: 'POST', body });
  },

  update(id: string, body: UpdateLinkRequest): Promise<LinkResponse> {
    return api(`${base}/${encodeURIComponent(id)}`, { method: 'PATCH', body });
  },

  archive(id: string): Promise<LinkResponse> {
    return api(`${base}/${encodeURIComponent(id)}/archive`, { method: 'POST' });
  },

  remove(id: string): Promise<void> {
    return api(`${base}/${encodeURIComponent(id)}`, { method: 'DELETE' });
  },

  versions(id: string, limit?: number): Promise<PagedResponse<LinkVersionSummary>> {
    return api(`${base}/${encodeURIComponent(id)}/versions`, { query: { limit } });
  },

  /**
   * FR-129. GET is used so nothing is written and the request is trivially repeatable. The query
   * names are the snake_case ones the route declares explicitly (ua, os_version, app_version,
   * click_id).
   */
  simulate(id: string, request: SimulateRequest, language?: string, signal?: AbortSignal): Promise<SimulateResponse> {
    return api(`${base}/${encodeURIComponent(id)}/simulate`, {
      query: {
        ua: request.user_agent,
        platform: request.platform,
        country: request.country,
        region: request.region,
        language: request.language,
        os_version: request.os_version,
        app_version: request.app_version,
        channel: request.channel,
        at: request.at,
        click_id: request.click_id,
      },
      language,
      signal,
    });
  },

  templates: {
    list(signal?: AbortSignal): Promise<PagedResponse<LinkTemplateResponse>> {
      return api(`${base}/templates`, { signal });
    },
    create(body: LinkTemplateRequest): Promise<LinkTemplateResponse> {
      return api(`${base}/templates`, { method: 'POST', body });
    },
    remove(id: string): Promise<void> {
      return api(`${base}/templates/${encodeURIComponent(id)}`, { method: 'DELETE' });
    },
  },
};

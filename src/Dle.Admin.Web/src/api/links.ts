import { ApiError, api, createIdempotencyKey, getConnection } from './client';
import type {
  BulkLinkItem,
  BulkLinkResult,
  CreateLinkRequest,
  LinkListQuery,
  LinkResponse,
  LinkVersionSummary,
  PagedResponse,
  ProblemDocument,
  SimulateRequest,
  SimulateResponse,
  UpdateLinkRequest,
} from './types';

const base = '/api/v1/links';

async function readBulkProblem(response: Response): Promise<ProblemDocument | undefined> {
  const ct = response.headers.get('content-type') ?? '';
  if (!ct.includes('json')) {
    return undefined;
  }
  try {
    return (await response.json()) as ProblemDocument;
  } catch {
    return undefined;
  }
}

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
   * GET is used so nothing is written and the request is trivially repeatable. The query
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

  /** Streams BulkLinkItem rows as application/x-ndjson and emits per-row results. */
  async bulkCreateNdjson(
    items: readonly BulkLinkItem[],
    onResult?: (result: BulkLinkResult) => void,
    signal?: AbortSignal,
  ): Promise<BulkLinkResult[]> {
    const connection = getConnection();
    if (!connection) {
      throw new ApiError({ status: 0, key: 'no_credential', message: 'No API key configured.' });
    }

    const body = items.map((item) => JSON.stringify(item)).join('\n');
    let response: Response;
    try {
      response = await fetch(`${connection.baseUrl}${base}/bulk`, {
        method: 'POST',
        headers: {
          Accept: 'application/x-ndjson, application/problem+json',
          'Content-Type': 'application/x-ndjson',
          Authorization: `Bearer ${connection.apiKey}`,
          'Idempotency-Key': createIdempotencyKey(),
        },
        body,
        signal,
        credentials: 'omit',
        referrerPolicy: 'no-referrer',
      });
    } catch (error) {
      if (error instanceof DOMException && error.name === 'AbortError') {
        throw error;
      }
      throw new ApiError({ status: 0, key: 'network', message: 'The control plane could not be reached.' });
    }

    if (!response.ok) {
      const problem = await readBulkProblem(response);
      throw new ApiError({ status: response.status, problem });
    }

    const results: BulkLinkResult[] = [];
    const emitLine = (line: string) => {
      const trimmed = line.trim();
      if (!trimmed) {
        return;
      }
      const parsed = JSON.parse(trimmed) as BulkLinkResult;
      results.push(parsed);
      onResult?.(parsed);
    };

    const reader = response.body?.getReader();
    if (!reader) {
      const text = await response.text();
      for (const line of text.split(/\r?\n/)) {
        emitLine(line);
      }
      return results;
    }

    const decoder = new TextDecoder();
    let buffer = '';
    for (;;) {
      const { value, done } = await reader.read();
      if (done) {
        break;
      }
      buffer += decoder.decode(value, { stream: true });
      const lines = buffer.split(/\r?\n/);
      buffer = lines.pop() ?? '';
      for (const line of lines) {
        emitLine(line);
      }
    }
    buffer += decoder.decode();
    emitLine(buffer);
    return results;
  },
};
